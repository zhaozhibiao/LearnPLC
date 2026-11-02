// SPDX-License-Identifier: MIT
#define ST_AXIS_WORKER_NO_MAIN
#include "axis_worker.cpp"
#include "gateway_queue.hpp"
namespace stworkbench::gateway {
using namespace axis;
static bool terminal(const Json &o) {
  std::string s = o.at("state");
  return s == "Completed" || s == "Failed" || s == "Aborted" || s == "Unknown";
}
static std::string identifier(const Json &j) {
  if (!j.is_string())
    throw std::invalid_argument("Identifier required");
  std::string s = j;
  if (s.empty() || s.size() > 128)
    throw std::invalid_argument("Identifier size");
  for (unsigned char c : s)
    if (c < 33 || c > 127)
      throw std::invalid_argument("Printable ASCII identifier required");
  return s;
}
struct Gateway {
  Worker worker;
  std::set<std::string> requests;
  std::map<std::string, Json> operations;
  bool closed = false;
  double deadline = 0, heartbeat = 0, lease_until = 0;
  unsigned lease_ms = 0;
  std::optional<double> jog_deadline;
  Gateway(Manifest m, Backend &b, std::string mode)
      : worker(std::move(m), b, std::move(mode)) {
    worker.probe();
    healthy();
  }
  void healthy() {
    if (worker.failed)
      throw ProtocolError("Invalid controller snapshot");
  }
  void poll() {
    worker.poll();
    healthy();
    for (size_t i = 0; i < worker.manifest.axes.size(); ++i) {
      const auto &sample = worker.slots[i].sample;
      if (!sample.contains("homed") || !sample["homed"].is_boolean())
        throw ProtocolError("Invalid actual homed flag");
    }
    for (auto &[id, op] : operations) {
      if (terminal(op))
        continue;
      size_t i = op["axisIndex"];
      auto &s = worker.slots[i];
      bool completed =
          s.done && (worker.mode != "EcMotion" ||
                     worker.cycle > op["submittedCycle"].get<uint64_t>());
      if (s.done && !completed) {
        s.done = false;
        s.busy = true;
      }
      op["state"] = s.error     ? "Failed"
                    : s.aborted ? "Aborted"
                    : completed ? "Completed"
                                : "Running";
      op["sampleCycle"] = worker.cycle;
      op["sample"] = s.sample;
    }
  }
  void stop(size_t i) {
    if (worker.direct(i, 7).value("accepted", false) != true)
      throw ProtocolError("Stop unacknowledged");
    for (auto &[id, o] : operations)
      if (o["axisIndex"] == i && !terminal(o))
        o["state"] = "Aborted";
  }
  void release() {
    if (!worker.lease.empty()) {
      auto r = worker.backend.request("ReleaseControlRequest",
                                      {{"leaseToken", worker.lease}});
      if (r.value("accepted", false) != true)
        throw ProtocolError("Release unacknowledged");
      worker.lease.clear();
    }
    worker.owned.clear();
    jog_deadline.reset();
  }
  void close(bool unknown = true) {
    if (closed)
      return;
    closed = true;
    for (auto i : worker.owned)
      try {
        stop(i);
      } catch (...) {
        unknown = true;
      }
    try {
      release();
    } catch (...) {
      worker.lease.clear();
      unknown = true;
    }
    for (auto &[id, o] : operations)
      if (!terminal(o) || (unknown && o["state"] == "Aborted"))
        o["state"] = unknown ? "Unknown" : "Aborted";
    worker.ready = false;
  }
  ~Gateway() { close(); }
  void tick() {
    if (closed)
      return;
    try {
      double t = now();
      if (!worker.lease.empty() && t >= deadline) {
        close(false);
        return;
      }
      if (jog_deadline && t >= *jog_deadline) {
        for (auto i : worker.owned)
          stop(i);
        release();
      }
      if (!worker.lease.empty() && !jog_deadline && t >= heartbeat) {
        if (worker.backend
                .request("HeartbeatRequest", {{"leaseToken", worker.lease}})
                .value("accepted", false) != true)
          throw ProtocolError("Heartbeat refused");
        lease_until = now() + lease_ms / 1000.;
        heartbeat = now() + lease_ms / 3000.;
      }
      poll();
    } catch (...) {
      close();
    }
  }
  Json handle(const Json &r) {
    Json result = {
        {"requestId", r.is_object() ? r.value("requestId", Json()) : Json()},
        {"success", false},
        {"state", "Rejected"}};
    try {
      if (!r.is_object())
        throw std::invalid_argument("Request must be object");
      auto rid = identifier(r.value("requestId", Json()));
      if (requests.count(rid))
        throw std::invalid_argument("Duplicate requestId; no replay");
      if (requests.size() >= 4096) {
        close(false);
        throw std::invalid_argument("Request capacity exhausted");
      }
      requests.insert(rid);
      std::string action = r.value("action", std::string());
      static const std::map<std::string, std::set<std::string>> fields = {
          {"capabilities", {}},
          {"status", {}},
          {"acquire", {"durationMilliseconds"}},
          {"release", {}},
          {"operation", {"operationId"}},
          {"stop", {"axis", "operationId"}},
          {"execute",
           {"operationId", "axis", "kind", "position", "velocity",
            "durationMilliseconds"}}};
      auto f = fields.find(action);
      if (f == fields.end())
        throw std::invalid_argument("Unsupported action");
      for (auto it = r.begin(); it != r.end(); ++it)
        if (it.key() != "requestId" && it.key() != "action" &&
            !f->second.count(it.key()))
          throw std::invalid_argument("Unsupported request field");
      auto success = [&](std::string state) {
        result["success"] = true;
        result["state"] = state;
      };
      if (action == "operation") {
        auto id = identifier(r.value("operationId", Json()));
        if (!operations.count(id))
          throw std::invalid_argument("Unknown operationId");
        success(operations[id]["state"]);
        result["operation"] = operations[id];
        return result;
      }
      if (closed)
        throw std::invalid_argument("Session closed; no reconnect or replay");
      if (action == "capabilities") {
        success("Ready");
        result["capabilities"] = worker.capabilities();
        result["leaseHeld"] = !worker.lease.empty();
        result["maximumControlMilliseconds"] = 30000;
        result["minimumJogMilliseconds"] = 250;
        result["maximumJogMilliseconds"] = 30000;
        result["PhysicalTimedJog"] = false;
        result["SimulatorTimedJog"] = worker.mode == "EcMotionSimulator";
        return result;
      }
      if ((action == "execute" || action == "stop") &&
          r.contains("operationId") && r["operationId"].is_string() &&
          operations.count(r["operationId"]))
        throw std::invalid_argument("Duplicate operationId");
      poll();
      if (action == "status") {
        success("Ready");
        result["sampleCycle"] = worker.cycle;
        result["manifestSha256"] = worker.manifest.digest;
        result["backend"] = worker.mode;
        result["axes"] = Json::array();
        for (size_t i = 0; i < worker.manifest.axes.size(); ++i) {
          auto &m = worker.manifest.axes[i];
          result["axes"].push_back({{"axis", m["Name"]},
                                    {"unit", m["Unit"]},
                                    {"sample", worker.slots[i].sample}});
        }
        return result;
      }
      if (action == "acquire") {
        if (!worker.lease.empty())
          throw std::invalid_argument("Already holds control");
        auto duration = r.value("durationMilliseconds", Json(1000));
        if (!integer(duration) || duration < 250 || duration > 30000)
          throw std::invalid_argument("Lease duration 250..30000 ms required");
        auto grant = worker.backend.request(
            "AcquireControlRequest",
            {{"clientName", "ST Workbench MCP motion session"},
             {"requestedLeaseMilliseconds", duration}},
            "AcquireControlResponse");
        if (grant.contains("leaseToken") && grant["leaseToken"].is_string())
          worker.lease = grant["leaseToken"];
        auto granted = grant.value("grantedLeaseMilliseconds", Json());
        if (worker.lease.empty() || !integer(granted) || granted < 250 ||
            granted > duration)
          throw ProtocolError("Invalid lease grant");
        double remaining = expiry_remaining(grant.at("expiresAt"));
        if (remaining <= 0 || remaining > duration.get<double>() / 1000 + .05)
          throw ProtocolError("Invalid lease expiry");
        lease_ms = granted;
        lease_until = now() + remaining;
        heartbeat = now() + lease_ms / 3000.;
        deadline = now() + 30;
        success("Acquired");
        result["remainingMilliseconds"] = 30000;
        return result;
      }
      if (worker.lease.empty())
        throw std::invalid_argument("Explicit acquire required");
      if (now() >= deadline) {
        close(false);
        throw std::invalid_argument("Control deadline elapsed");
      }
      if (action == "release") {
        for (auto i : worker.owned)
          stop(i);
        release();
        success("Released");
        return result;
      }
      auto id = identifier(r.value("operationId", Json()));
      if (operations.count(id))
        throw std::invalid_argument("Duplicate operationId");
      size_t index = worker.manifest.axes.size();
      for (size_t i = 0; i < worker.manifest.axes.size(); ++i)
        if (worker.manifest.axes[i]["Name"] == r.value("axis", Json()))
          index = i;
      if (index == worker.manifest.axes.size())
        throw std::invalid_argument("Axis absent from active manifest");
      std::string kind =
          action == "stop" ? "Stop" : r.value("kind", std::string());
      auto it = std::find(kinds.begin() + 1, kinds.end(), kind);
      if (it == kinds.end() || (action == "execute" && kind == "Stop"))
        throw std::invalid_argument("Unsupported command");
      unsigned number = it - kinds.begin();
      auto &m = worker.manifest.axes[index];
      auto &s = worker.slots[index];
      if (kind != "Stop") {
        for (auto &[oid, o] : operations)
          if (!terminal(o) && o["axisIndex"] == index)
            throw std::invalid_argument("Axis busy");
        if (jog_deadline)
          throw std::invalid_argument("Dedicated Jog lease busy");
      }
      auto finite = [](const Json &v) {
        if (!v.is_number() || v.is_boolean() || !std::isfinite(v.get<double>()))
          throw std::invalid_argument("Finite numeric parameter required");
        return v.get<double>();
      };
      double position = finite(r.value("position", Json(0))),
             velocity = finite(r.value("velocity", Json(0)));
      if (worker.mode == "EcMotion" &&
          (number == 4 || number == 5 || number == 6))
        for (auto key :
             {"MaximumAcceleration", "MaximumDeceleration", "MaximumJerk"})
          if (m.contains(key) && !m[key].is_null())
            throw std::invalid_argument(
                "capability_missing: dynamic parameters unproven");
      if (number == 4 &&
          (s.sample["state"] != "Standstill" ||
           s.sample["actualVelocity"] != 0 || yes(s.sample, "fault")))
        throw std::invalid_argument("Home requires stationary enabled axis");
      if (number == 5 || number == 6) {
        if (s.sample["state"] != "Standstill" ||
            s.sample["actualVelocity"] != 0 || !yes(s.sample, "homed"))
          throw std::invalid_argument(
              "Motion requires homed stationary enabled axis");
        if (velocity == 0 || (number == 5 && velocity < 0))
          throw std::invalid_argument("Invalid velocity");
        if (m.contains("MaximumVelocity") && !m["MaximumVelocity"].is_null() &&
            std::abs(velocity) > numeric(m["MaximumVelocity"]))
          throw std::invalid_argument("Velocity limit");
      }
      if (number == 5 && (position < numeric(m["MinPosition"]) ||
                          position > numeric(m["MaxPosition"])))
        throw std::invalid_argument("Position limit");
      if (number != 7 && number != 2 && number != 3 &&
          (!worker.ready || (worker.mode == "EcMotion" && !worker.physical())))
        throw std::invalid_argument("capability_missing");
      if (number == 6) {
        if (worker.mode == "EcMotion")
          throw std::invalid_argument("capability_missing: physical timed Jog");
        auto duration = r.value("durationMilliseconds", Json());
        if (!integer(duration) || duration < 250 || duration > 30000 ||
            lease_until > deadline)
          throw std::invalid_argument("Invalid Jog duration");
        for (auto &[oid, o] : operations)
          if (!terminal(o))
            throw std::invalid_argument("Jog requires exclusive idle session");
        double target = numeric(s.sample["actualPosition"]) +
                        velocity * duration.get<double>() / 1000;
        if (target < numeric(m["MinPosition"]) ||
            target > numeric(m["MaxPosition"]))
          throw std::invalid_argument("Jog displacement limit");
        double remaining = lease_until - now();
        if (remaining <= 0 || remaining > duration.get<double>() / 1000)
          throw std::invalid_argument("Jog lease exceeds duration");
        jog_deadline = lease_until;
      }
      Json op = {
          {"operationId", id},           {"axis", m["Name"]},
          {"axisIndex", index},          {"kind", kind},
          {"state", "Accepted"},         {"motionId", 0},
          {"appliedCycle", 0},           {"backend", worker.mode},
          {"sampleCycle", worker.cycle}, {"submittedCycle", worker.cycle}};
      operations[id] = op;
      worker.owned.insert(index);
      auto previous = s.sample.value("motionId", Json());
      Json response;
      uint64_t applied = 0;
      if (number == 7) {
        stop(index);
        operations[id]["state"] = "Accepted";
        response = {{"accepted", true}};
      } else if (number == 4) {
        auto home = worker.home(index);
        response = home.first;
        applied = home.second;
      } else
        response = worker.direct(index, number, position, velocity);
      if (!yes(response, "accepted"))
        throw ProtocolError("Command acceptance unproven");
      auto motion = response.value("motionId", Json(0));
      if (number == 5 && (!uint(motion) || motion == 0 || motion == previous))
        throw ProtocolError("New motion ID absent");
      auto sample = s.sample;
      s = Slot{};
      s.kind = number;
      s.busy = true;
      s.motion_id = u64(motion);
      s.applied_cycle = applied;
      s.reset_deadline = now() + 5;
      s.sample = sample;
      operations[id]["motionId"] = motion;
      operations[id]["appliedCycle"] = applied;
      if (number == 6)
        operations[id]["maximumRemainingMilliseconds"] =
            std::max(0, int((*jog_deadline - now()) * 1000));
      success("Accepted");
      result["operation"] = operations[id];
      return result;
    } catch (const std::invalid_argument &e) {
      result["message"] = e.what();
      return result;
    } catch (const BackendError &e) {
      if (std::string(e.what()) == "control_busy" && worker.lease.empty()) {
        result["state"] = "Conflict";
        result["message"] = "Controller owned by another session";
        return result;
      }
      close();
      result["state"] = "Unknown";
      result["message"] = "Controller rejected session; no retry";
      return result;
    } catch (...) {
      close();
      result["state"] = "Unknown";
      result["message"] = "Controller result unproven; no retry";
      return result;
    }
  }
  static double expiry_remaining(const Json &value) {
    return -Worker::timestamp_age(value);
  }
};
} // namespace stworkbench::gateway
#ifndef ST_GATEWAY_NO_MAIN
int main(int argc, char **argv) {
  using namespace stworkbench;
  using namespace stworkbench::axis;
  try {
    std::string manifest, path, mode;
    for (int i = 1; i < argc; ++i) {
      std::string a = argv[i];
      if (i + 1 >= argc)
        throw std::runtime_error("Missing option");
      if (a == "--manifest")
        manifest = argv[++i];
      else if (a == "--controller-socket")
        path = argv[++i];
      else if (a == "--backend")
        mode = argv[++i];
      else
        throw std::runtime_error("Unknown option");
    }
    if (manifest.empty() || path.empty() ||
        (mode != "EcMotion" && mode != "EcMotionSimulator"))
      throw std::runtime_error(
          "Manifest, controller socket and explicit backend required");
    Fd socketfd(socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0));
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    if (path.size() >= sizeof(address.sun_path))
      throw ProtocolError("Socket path length");
    memcpy(address.sun_path, path.c_str(), path.size() + 1);
    if (connect(socketfd.get(), reinterpret_cast<sockaddr *>(&address),
                sizeof(address)))
      throw ProtocolError("Controller connect");
    NativeClient backend(socketfd.release());
    auto manifest_bytes = read_file(manifest, 1048576);
    gateway::Gateway gateway(
        Manifest::from(parse_json(manifest_bytes), sha256(manifest_bytes)),
        backend, mode);
    fcntl(STDIN_FILENO, F_SETFL, fcntl(STDIN_FILENO, F_GETFL) | O_NONBLOCK);
    fcntl(STDOUT_FILENO, F_SETFL, fcntl(STDOUT_FILENO, F_GETFL) | O_NONBLOCK);
    signal(SIGPIPE, SIG_IGN);
    using gateway::Pending;
    gateway::StopBarrier stop_barrier;
    std::vector<Pending> inbox;
    std::vector<std::string> outbox;
    std::string incoming;
    size_t output_offset = 0;
    uint64_t sequence = 0;
    bool transport_failed = false;
    while (!gateway.closed && !transport_failed) {
      gateway.tick();
      pollfd fds[2] = {{STDIN_FILENO, POLLIN, 0},
                       {STDOUT_FILENO, short(outbox.empty() ? 0 : POLLOUT), 0}};
      if (poll(fds, 2, 25) < 0 && errno != EINTR)
        break;
      if (fds[0].revents & POLLIN) {
        char bytes[4096];
        ssize_t n = read(STDIN_FILENO, bytes, sizeof(bytes));
        if (n == 0)
          break;
        if (n < 0 && errno != EAGAIN && errno != EINTR)
          break;
        if (n > 0) {
          incoming.append(bytes, n);
          for (;;) {
            auto end = incoming.find('\n');
            if (end == std::string::npos)
              break;
            if (end + 1 > 65536 || inbox.size() >= 16) {
              transport_failed = true;
              break;
            }
            Json request;
            try {
              request = parse_json(incoming.substr(0, end));
            } catch (...) {
              request = nullptr;
            }
            inbox.push_back({sequence++, request});
            incoming.erase(0, end + 1);
          }
          if (incoming.size() > 65536)
            transport_failed = true;
        }
      }
      if (fds[0].revents & (POLLHUP | POLLERR | POLLNVAL))
        break;
      if (fds[1].revents & (POLLHUP | POLLERR | POLLNVAL))
        break;
      if (fds[1].revents & POLLOUT) {
        auto &s = outbox.front();
        ssize_t n = write(STDOUT_FILENO, s.data() + output_offset,
                          s.size() - output_offset);
        if (n > 0) {
          output_offset += n;
          if (output_offset == s.size()) {
            outbox.erase(outbox.begin());
            output_offset = 0;
          }
        } else if (n == 0 || (errno != EAGAIN && errno != EINTR))
          break;
      }
      if (!inbox.empty() && !transport_failed) {
        auto pending = stop_barrier.pop(inbox);
        Json response;
        if (!pending.request.is_object()) {
          response = {{"requestId", nullptr},
                      {"success", false},
                      {"state", "Rejected"},
                      {"message", "Invalid bounded JSON request"}};
        } else {
          if (stop_barrier.invalidated(pending))
            response = {
                {"requestId", pending.request.value("requestId", Json())},
                {"success", false},
                {"state", "Aborted"},
                {"message",
                 "Queued control invalidated by newer stop/release"}};
          else
            response = gateway.handle(pending.request);
        }
        if (outbox.size() >= 8)
          break;
        outbox.push_back(response.dump() + "\n");
      }
    }
    gateway.close();
    return 0;
  } catch (const std::exception &e) {
    std::cerr
        << "Motion gateway failed to establish verified controller session: "
        << e.what() << '\n';
    return 1;
  }
}
#endif

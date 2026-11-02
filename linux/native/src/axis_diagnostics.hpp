// SPDX-License-Identifier: MIT
#pragma once
#include "hardware_private.hpp"
#include <cmath>
#include <functional>
#include <map>
#include <numeric>
namespace stworkbench::diagnostics {
using hardware::integer;
using hardware::require;
inline void probe_options(int samples, int interval) {
  require(samples >= 2 && samples <= 1000 && interval >= 1 &&
              interval <= 1000 && samples * interval <= 60000,
          "Use 2..1000 samples and duration <=60 seconds");
}
inline double finite_number(const Json &j) {
  require(j.is_number() && !j.is_boolean(), "Finite numeric value required");
  auto n = j.get<double>();
  require(std::isfinite(n), "Finite numeric value required");
  return n;
}
inline std::uint64_t counter(const Json &j) {
  require(integer(j) && j > 0, "Invalid feedback counter");
  return j.get<std::uint64_t>();
}
class StationaryProof {
  struct Policy {
    double position, velocity;
    std::uint64_t dwell;
    bool stability;
  };
  std::map<std::string, Policy> policies;
  std::map<std::string, std::pair<double, double>> spans;
  std::uint64_t duration = 0, first = 0, previous_cycle = 0, previous_stamp = 0;

public:
  explicit StationaryProof(const Json &machine) {
    auto axes = machine.value("axes", Json());
    require(axes.is_array() && !axes.empty() && axes.size() <= 64,
            "Missing trusted stationary policies");
    for (auto &a : axes) {
      require(a.is_object(), "Invalid axis stationary policy");
      auto n = a.value("name", Json());
      auto p = a.value("motionCompletion", Json());
      require(n.is_string() && !n.get<std::string>().empty() && p.is_object(),
              "Invalid stationary policy");
      auto position = finite_number(p.value("positionTolerance", Json()));
      auto velocity = finite_number(p.value("velocityTolerance", Json()));
      auto d = p.value("dwellMilliseconds", Json());
      auto stability = p.value("positionStabilityCompletion", Json());
      require(position > 0 && velocity > 0 && integer(d) && d >= 1 &&
                  d <= 60000 && stability.is_boolean(),
              "Invalid stationary tolerance or dwell");
      auto dwell = d.get<std::uint64_t>() * 1000000ULL;
      require(
          policies
              .emplace(n.get<std::string>(),
                       Policy{position, velocity, dwell, stability.get<bool>()})
              .second,
          "Duplicate stationary policy");
      duration = std::max(duration, std::uint64_t(dwell));
    }
  }
  std::uint64_t duration_ns() const { return duration; }
  bool observe(const Json &sample, std::uint64_t now) {
    require(sample.value("runtimeReady", Json()) == true &&
                sample.value("ioReady", Json()) == true &&
                sample.value("outputsAtConfiguredSafeState", Json()) == true,
            "Recovery feedback is unsafe");
    auto cycle = counter(sample.value("sampleCycle", Json()));
    auto stamp = counter(sample.value("sampleMonotonicNs", Json()));
    require(now >= stamp && now - stamp <= 500000000ULL,
            "Recovery feedback stale or future");
    require(!previous_stamp ||
                (cycle > previous_cycle && stamp > previous_stamp),
            "Recovery feedback is not advancing");
    auto axes = sample.value("axes", Json());
    require(axes.is_array() && axes.size() == policies.size(),
            "Every physical axis needs stationary proof");
    std::set<std::string> names;
    for (auto &a : axes) {
      require(a.is_object() && a.value("name", Json()).is_string(),
              "Invalid recovery axis");
      auto name = a["name"].get<std::string>();
      require(policies.contains(name) && names.insert(name).second,
              "Recovery axis mapping differs");
      auto word = a.value("driveStatusword", Json());
      require((a.value("state", Json()) == "Disabled" ||
               a.value("state", Json()) == "ErrorStop") &&
                  a.value("motionBusy", Json()) == false && integer(word) &&
                  word >= 0 && word <= 65535 && !(word.get<unsigned>() & 4),
              "Axis torque or motion is unconfirmed");
      auto position = finite_number(a.value("actualPosition", Json()));
      auto velocity = finite_number(a.value("actualVelocity", Json()));
      auto [it, inserted] =
          spans.emplace(name, std::make_pair(position, position));
      it->second.first = std::min(it->second.first, position);
      it->second.second = std::max(it->second.second, position);
      auto p = policies.at(name);
      double limit = p.position;
      if (p.stability)
        limit = std::min(limit, p.velocity * double(p.dwell) / 1e9);
      require(it->second.second - it->second.first <= limit &&
                  (p.stability || std::abs(velocity) <= p.velocity),
              "Axis not stationary under applied policy");
    }
    if (!first)
      first = stamp;
    previous_stamp = stamp;
    previous_cycle = cycle;
    return stamp - first >= duration;
  }
};
class ReadonlyNativeClient {
  Fd stream;
  std::uint64_t sequence = 0;

public:
  explicit ReadonlyNativeClient(const std::string &endpoint) {
    require(!endpoint.empty() && endpoint[0] == '/' && endpoint.size() < 104 &&
                endpoint.find('\0') == endpoint.npos,
            "Private Unix socket required");
    stream.reset(
        ::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0));
    require(bool(stream), "Cannot create native socket");
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    std::memcpy(address.sun_path, endpoint.c_str(), endpoint.size() + 1);
    if (::connect(stream.get(), reinterpret_cast<sockaddr *>(&address),
                  sizeof address) != 0) {
      require(errno == EINPROGRESS || errno == EAGAIN, "Native connect failed");
      pollfd p{stream.get(), POLLOUT, 0};
      require(::poll(&p, 1, 200) > 0, "Native connect deadline");
      int error = 0;
      socklen_t size = sizeof error;
      require(::getsockopt(stream.get(), SOL_SOCKET, SO_ERROR, &error, &size) ==
                      0 &&
                  error == 0,
              "Native connect failed");
    }
  }
  Json request(const std::string &kind, const Json &payload,
               const std::string &expected) {
    require(
        (kind == "HelloRequest" && expected == "HelloResponse") ||
            (kind == "ReadSnapshotRequest" && expected == "SnapshotResponse"),
        "Readonly diagnostic forbids control requests");
    require(bool(stream),
            "Native diagnostic channel requires re-establishment");
    require(sequence < UINT64_MAX - 1, "Native sequence exhausted");
    auto next = sequence + 1;
    auto id = std::to_string(::getpid()) + "-" + std::to_string(monotonic_ns());
    Json envelope = {{"protocolVersion", 4},
                     {"kind", kind},
                     {"requestId", id},
                     {"sequence", next},
                     {"payload", payload}};
    auto bytes = envelope.dump();
    require(!bytes.empty() && bytes.size() <= 1024 * 1024,
            "Outgoing diagnostic frame exceeds capacity");
    sequence = next;
    try {
      std::uint32_t n = bytes.size();
      char header[4] = {char(n), char(n >> 8), char(n >> 16), char(n >> 24)};
      auto deadline = monotonic_ns() + 200000000LL;
      hardware::transfer(stream.get(), header, 4, true, deadline);
      hardware::transfer(stream.get(), bytes.data(), bytes.size(), true,
                         deadline);
      hardware::transfer(stream.get(), header, 4, false, deadline);
      auto u = reinterpret_cast<unsigned char *>(header);
      n = std::uint32_t(u[0]) | (std::uint32_t(u[1]) << 8) |
          (std::uint32_t(u[2]) << 16) | (std::uint32_t(u[3]) << 24);
      require(n > 0 && n <= 1024 * 1024,
              "Native diagnostic frame exceeds capacity");
      bytes.assign(n, '\0');
      hardware::transfer(stream.get(), bytes.data(), bytes.size(), false,
                         deadline);
      auto response = hardware::object(bytes);
      require(response.value("protocolVersion", Json()) == 4 &&
                  response.value("kind", Json()) == expected &&
                  response.value("requestId", Json()) == id &&
                  response.value("sequence", Json()) == next &&
                  response.value("payload", Json()).is_object(),
              "Native diagnostic response identity mismatch");
      return response["payload"];
    } catch (...) {
      stream.reset();
      throw;
    }
  }
};
using Request =
    std::function<Json(const std::string &, const Json &, const std::string &)>;
inline Json measure(
    const Request &request, int samples, int interval,
    const std::function<void(int)> &sleep =
        [](int ms) {
          std::this_thread::sleep_for(std::chrono::milliseconds(ms));
        },
    const std::function<std::int64_t()> &now = monotonic_ns) {
  probe_options(samples, interval);
  auto hello = request(
      "HelloRequest",
      {{"clientName", "Workbench readonly link probe"}, {"clientVersion", "1"}},
      "HelloResponse");
  require(integer(hello.value("protocolVersion", Json())) &&
              hello["protocolVersion"] == 4,
          "Native protocol mismatch");
  std::vector<double> times, ages;
  std::uint64_t first_cycle = 0, first_stamp = 0, last_cycle = 0,
                last_stamp = 0;
  Json last;
  for (int i = 0; i < samples; ++i) {
    if (i)
      sleep(interval);
    auto start = now();
    auto sample = request("ReadSnapshotRequest",
                          {{"includeDiagnostics", false}}, "SnapshotResponse");
    auto observed = now();
    auto cycle = counter(sample.value("sampleCycle", Json()));
    auto stamp = counter(sample.value("sampleMonotonicNs", Json()));
    require(observed >= 0 && stamp <= std::uint64_t(observed) &&
                std::uint64_t(observed) - stamp <= 500000000ULL,
            "Feedback stale or future");
    require(!i || (cycle >= last_cycle && stamp >= last_stamp &&
                   (cycle <= last_cycle || stamp > last_stamp)),
            "Feedback regressed or inconsistent");
    require(sample.value("axes", Json()).is_array(), "Missing native axes");
    double duration = double(observed - start) / 1e6;
    require(std::isfinite(duration) && duration >= 0, "Invalid timing clock");
    times.push_back(duration);
    ages.push_back(double(observed - stamp) / 1e6);
    if (!i) {
      first_cycle = cycle;
      first_stamp = stamp;
    }
    last_cycle = cycle;
    last_stamp = stamp;
    last = std::move(sample);
  }
  require(last_cycle > first_cycle && last_stamp > first_stamp,
          "Native feedback did not advance");
  auto ordered = times;
  std::sort(ordered.begin(), ordered.end());
  auto percentile = [&](double p) {
    return ordered[std::max<std::size_t>(
                       1, std::size_t(std::ceil(p * ordered.size()))) -
                   1];
  };
  int faults = 0;
  for (auto &a : last["axes"]) {
    require(a.is_object(), "Invalid native axis");
    auto f = a.value("fault", Json());
    if (!f.is_null() && f != false && f != "" && f != 0)
      ++faults;
  }
  return {{"Status", "PASS"},
          {"Measurement", "read_only_native_round_trip"},
          {"Samples", samples},
          {"MeanMilliseconds",
           std::accumulate(times.begin(), times.end(), 0.) / samples},
          {"P50Milliseconds", percentile(.5)},
          {"P95Milliseconds", percentile(.95)},
          {"P99Milliseconds", percentile(.99)},
          {"MaxMilliseconds", ordered.back()},
          {"MaxSnapshotAgeMilliseconds",
           *std::max_element(ages.begin(), ages.end())},
          {"FirstSampleCycle", first_cycle},
          {"LastSampleCycle", last_cycle},
          {"RuntimeReady", last.value("runtimeReady", Json()) == true},
          {"IoReady", last.value("ioReady", Json()) == true},
          {"MotionReady", last.value("motionReady", Json()) == true},
          {"AxisFaultCount", faults}};
}
} // namespace stworkbench::diagnostics

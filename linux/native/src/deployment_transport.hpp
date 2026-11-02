// SPDX-License-Identifier: MIT
#pragma once
#include "deployment_archive.hpp"
#include "hardware_private.hpp"
#include <sys/file.h>
#include <thread>
namespace stworkbench::deployment {
inline ProcessResult process(const std::vector<std::string> &args,
                             const fs::path &cwd = {}, int seconds = 30,
                             std::size_t maximum = 1024 * 1024,
                             bool checked = true) {
  auto r = run_process(args, cwd, std::chrono::seconds(seconds), maximum, false);
  require(!r.timed_out && !r.truncated,
          "Process exceeded bounded deadline/output");
  if (checked)
    require(r.exit_code == 0,
            "Process failed: " + fs::path(args.at(0)).filename().string());
  return r;
}
inline Json native(const std::string &kind, const Json &payload,
                   const std::string &expected) {
  return hardware::native(kind, payload, expected);
}
inline Json snapshot() {
  auto s = native("ReadSnapshotRequest", {{"includeDiagnostics", false}},
                  "SnapshotResponse");
  require(s.value("runtimeReady", Json{}) == true &&
              s.value("ioReady", Json{}) == true &&
              hardware::integer(s.value("sampleCycle", Json{})) &&
              s["sampleCycle"] > 0 &&
              hardware::integer(s.value("sampleMonotonicNs", Json{})) &&
              s["sampleMonotonicNs"] > 0,
          "Native runtime and IO must be ready");
  auto now = monotonic_ns();
  require(s["sampleMonotonicNs"].get<int64_t>() <= now &&
              now - s["sampleMonotonicNs"].get<int64_t>() <= 500000000,
          "Native snapshot stale or future");
  return s;
}
inline Json native_state() {
  auto h = native(
      "HelloRequest",
      {{"clientName", "stworkbench-deployment-native"}, {"clientVersion", "1"}},
      "HelloResponse");
  require(h.value("runtimeReady", Json{}) == true,
          "Loaded native runtime is not ready");
  identity(h.value("ethercatTopologyFingerprint", Json{}));
  auto a = snapshot();
  std::this_thread::sleep_for(std::chrono::milliseconds(20));
  auto b = snapshot();
  require(b["sampleCycle"] > a["sampleCycle"] &&
              b["sampleMonotonicNs"] > a["sampleMonotonicNs"],
          "Native hardware feedback is not advancing");
  return h;
}
struct TempDirectory {
  fs::path path;
  explicit TempDirectory(fs::path parent) {
    fs::create_directories(parent);
    std::string pattern = (parent / ".coordinator-XXXXXX").string();
    std::vector<char> p(pattern.begin(), pattern.end());
    p.push_back(0);
    require(mkdtemp(p.data()), "Cannot create private temporary directory");
    path = p.data();
  }
  ~TempDirectory() {
    std::error_code e;
    fs::remove_all(path, e);
  }
};
class Runtime {
  Json config_;
  TempDirectory temp_;
  std::string token_;

public:
  explicit Runtime(const Json &config)
      : config_(config),
        temp_(fs::path(config.at("prefix").get<std::string>())) {}
  Json call(const std::string &path, const Json &body = Json()) {
    require(path.starts_with("/api/") &&
                path.find_first_of("\r\n?#") == path.npos,
            "Invalid fixed Runtime route");
    if (token_.empty() && path != "/api/login") {
      auto credentials = parse_json(read_file(
          config_.at("credentials_file").get<std::string>(), 16384, true));
      auto login = call("/api/login", credentials);
      require(login.contains("access_token") &&
                  login["access_token"].is_string(),
              "Runtime login failed");
      token_ = login["access_token"];
      require(!token_.empty() && token_.size() <= 12000 &&
                  token_.find_first_not_of(
                      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123"
                      "456789-._~+/=") == token_.npos,
              "Invalid Runtime token");
      atomic_write(temp_.path / "header",
                   "Authorization: Bearer " + token_ + "\n");
    }
    std::vector<std::string> a = {"/usr/bin/curl",
                                  "-q",
                                  "--silent",
                                  "--show-error",
                                  "--fail",
                                  "--proto",
                                  "=https",
                                  "--max-time",
                                  "5",
                                  "--max-redirs",
                                  "0",
                                  "--noproxy",
                                  "*",
                                  "--cacert",
                                  config_.at("ca_file").get<std::string>(),
                                  "--header",
                                  "Content-Type: application/json"};
    if (!token_.empty())
      a.insert(a.end(), {"--header", "@" + (temp_.path / "header").string()});
    if (!body.is_null()) {
      atomic_write(temp_.path / "body", body.dump());
      a.insert(a.end(),
               {"--data-binary", "@" + (temp_.path / "body").string()});
    }
    require(
        config_.value("rest_origin", std::string("https://localhost:8443")) ==
            "https://localhost:8443",
        "Exact private Runtime HTTPS origin required");
    a.push_back("https://localhost:8443" + path);
    auto r = process(a, {}, 8, 4 * 1024 * 1024);
    auto j = parse_json(r.output);
    require(j.is_object(), "Invalid Runtime JSON response");
    return j;
  }
  std::string status() {
    auto v = call("/api/status").value("status", Json{});
    require(v.is_string(), "Missing Runtime state");
    auto s = v.get<std::string>();
    while (!s.empty() && (s.back() == '\r' || s.back() == '\n'))
      s.pop_back();
    require(s.starts_with("STATUS:"), "Invalid Runtime status prefix");
    s = s.substr(7);
    require(s == "STOPPED" || s == "RUNNING" || s == "EMPTY" ||
                s == "TRANSITIONING",
            "Unknown Runtime state");
    return s;
  }
  bool task_supported() {
    auto j = call("/api/workbench-task-scheduling");
    return j.value("interface", Json{}) == "workbench-task-scheduling-v1" &&
           j.value("supported", Json{}) == true;
  }
  std::string transition(const std::string &action) {
    require(action == "start" || action == "stop", "Invalid PLC transition");
    auto expected = action == "start" ? "RUNNING" : "STOPPED";
    const auto initial = status();
    if (initial == expected)
      return expected;
    require(action == "start" ? (initial == "STOPPED" || initial == "EMPTY")
                              : initial == "RUNNING",
            "Runtime cannot accept explicit transition from current state");
    call(action == "start" ? "/api/start-plc" : "/api/stop-plc");
    auto until = monotonic_ns() + 30000000000LL;
    while (monotonic_ns() < until) {
      const auto current = status();
      if (current == expected)
        return expected;
      require(current == "TRANSITIONING", "Runtime transition did not reach "
                                          "expected state; query actual state");
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    throw std::runtime_error(
        "Runtime transition result unknown; never retry automatically");
  }
};
inline void user_service(const std::string &action, const std::string &unit) {
  require(action == "start" || action == "stop", "Invalid service action");
  static const std::set<std::string> allowed = {
      "stworkbench-openplc.service", "stworkbench-is620n-axis.service",
      "stworkbench-axis-v2.service", "stworkbench-communication.service",
      "stworkbench-motion.service"};
  require(allowed.contains(unit), "Unapproved service unit");
  process({"/usr/bin/systemctl", "--user", action, unit}, {}, 30);
  if (action == "stop") {
    auto state = process({"/usr/bin/systemctl", "--user", "show", unit,
                          "--property=ActiveState", "--property=MainPID"})
                     .output;
    require(state.find("ActiveState=inactive") != state.npos &&
                state.find("MainPID=0") != state.npos,
            "Private service is not confirmed inactive");
  }
}
inline bool stop_loaded_worker(const std::string &unit) {
  auto properties = process({"/usr/bin/systemctl", "--user", "show", unit,
                             "--property=LoadState", "--property=ActiveState"},
                            {}, 10, 8192, false)
                        .output;
  if (properties.find("LoadState=not-found") != properties.npos)
    return false;
  require(properties.find("LoadState=loaded") != properties.npos,
          "Worker service state unconfirmed");
  bool active = properties.find("ActiveState=active\n") != properties.npos;
  user_service("stop", unit);
  return active;
}
inline Json broker(const std::string &action, const std::string &build,
                   const std::string &id) {
  require(action == "status" || action == "validate" || action == "apply",
          "Invalid broker action");
  Json request = {
      {"protocolVersion", 1}, {"requestId", id}, {"action", action}};
  if (action != "status")
    request["buildHash"] = identity(build);
  Fd fd(::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0));
  require(bool(fd), "Cannot open broker socket");
  sockaddr_un addr{};
  addr.sun_family = AF_UNIX;
  std::strcpy(addr.sun_path, "/run/st-workbench-hardware/control.sock");
  const auto deadline =
      monotonic_ns() + (action == "apply" ? 180000000000LL : 15000000000LL);
  if (::connect(fd.get(), reinterpret_cast<sockaddr *>(&addr), sizeof addr) !=
      0) {
    require(errno == EINPROGRESS || errno == EAGAIN,
            "Hardware broker unavailable");
    pollfd p{fd.get(), POLLOUT, 0};
    require(::poll(&p, 1, 3000) > 0, "Hardware broker connect timeout");
    int error = 0;
    socklen_t n = sizeof error;
    require(::getsockopt(fd.get(), SOL_SOCKET, SO_ERROR, &error, &n) == 0 &&
                !error,
            "Hardware broker connect failed");
  }
  ucred peer{};
  socklen_t n = sizeof peer;
  require(::getsockopt(fd.get(), SOL_SOCKET, SO_PEERCRED, &peer, &n) == 0 &&
              peer.uid == 0,
          "Hardware broker must be root owned");
  auto data = request.dump();
  uint32_t size = data.size();
  char h[4] = {char(size), char(size >> 8), char(size >> 16), char(size >> 24)};
  hardware::transfer(fd.get(), h, 4, true, deadline);
  hardware::transfer(fd.get(), data.data(), data.size(), true, deadline);
  hardware::transfer(fd.get(), h, 4, false, deadline);
  size = uint8_t(h[0]) | (uint32_t(uint8_t(h[1])) << 8) |
         (uint32_t(uint8_t(h[2])) << 16) | (uint32_t(uint8_t(h[3])) << 24);
  require(size && size <= 1024 * 1024, "Invalid broker response capacity");
  data.assign(size, '\0');
  hardware::transfer(fd.get(), data.data(), size, false, deadline);
  auto r = parse_json(data);
  require(r.value("protocolVersion", Json{}) == 1 &&
              r.value("requestId", Json{}) == id,
          "Broker response identity differs");
  require(r.value("success", Json{}) == true &&
              r.value("result", Json{}).is_object(),
          "Hardware broker rejected or unconfirmed; query actual state");
  return r["result"];
}
} // namespace stworkbench::deployment

// SPDX-License-Identifier: MIT
#pragma once
#include "deployment_transport.hpp"
#define ST_AXIS_WORKER_NO_MAIN
#include "axis_diagnostics.hpp"
#include "axis_worker.cpp"
#include "communication_engine.hpp"
namespace stworkbench::deployment {
inline std::string axis_interface(const Bundle &b) {
  return b.manifest.value("AxisInterfaceVersion",
                          std::string("workbench-axis-v1"));
}
struct Channel {
  std::string endpoint, unit, environment, backend, target, manifest;
};
inline Channel channel(const Json &config, const std::string &kind) {
  if (kind == "workbench-axis-v1")
    return {config.at("axis_socket_path"),
            "stworkbench-is620n-axis.service",
            "STWORKBENCH_AXIS_SOCKET",
            config.at("axis_backend"),
            "st-axis-worker",
            "axis-manifest.json"};
  if (kind == "workbench-axis-v2") {
    require(config.at("axis_v2_socket_path") ==
                "/home/a/.local/share/st-workbench/axis-v2/axis.sock",
            "Explicit separate v2 channel required");
    return {config.at("axis_v2_socket_path"),
            "stworkbench-axis-v2.service",
            "STWORKBENCH_AXIS_V2_SOCKET",
            config.at("axis_backend"),
            "st-axis-worker",
            "axis-manifest.json"};
  }
  if (kind == "workbench-communication-v1")
    return {config.at("communication_socket_path"),
            "stworkbench-communication.service",
            "STWORKBENCH_COMM_SOCKET",
            "NativeClient",
            "st-communication-worker",
            "communication-manifest.json"};
  require(kind == "workbench-continuous-v1", "Unsupported worker interface");
  return {config.at("motion_socket_path"),
          "stworkbench-motion.service",
          "STWORKBENCH_MOTION_SOCKET",
          "VirtualContinuous",
          "st-path-worker",
          "paths/motion-manifest.json"};
}
inline void validate_dependencies(const Bundle &b) {
  auto has = [&](const char *k) {
    return !b.manifest.value(k, Json{}).is_null();
  };
  if (has("AxisInterfaceVersion")) {
    require(std::find(b.manifest.at("NativeWorkerTargets").begin(),
                      b.manifest.at("NativeWorkerTargets").end(),
                      "st-axis-worker") !=
                b.manifest.at("NativeWorkerTargets").end(),
            "Native axis target missing");
    auto interface = b.manifest.at("AxisInterfaceVersion");
    require(interface == "workbench-axis-v1" ||
                interface == "workbench-axis-v2",
            "Unsupported axis ABI");
    for (auto n : {"axis_control.st", "axis-constants.st", "axis_bridge.hpp",
                   "axis_bridge.cpp", "generated_axis_scan_shim.cpp",
                   "axis_lifecycle.inc", "axis-manifest.json"})
      require(b.files.contains(n), "Incomplete axis dependency bundle");
    auto data = parse_json(b.files.at("axis-manifest.json"));
    auto m =
        axis::Manifest::from(data, sha256(b.files.at("axis-manifest.json")));
    require(data.at("InterfaceVersion") == interface,
            "Axis package and image interface differ");
    std::string expected = "VAR_GLOBAL CONSTANT\n";
    for (auto &a : data.at("Axes"))
      expected += "    AXIS_" + a.at("Name").get<std::string>() +
                  " : AXIS_REF := " + std::to_string(a.at("Index").get<int>()) +
                  ";\n";
    expected += "END_VAR\n";
    auto actual = b.files.at("axis-constants.st");
    actual = std::regex_replace(actual, std::regex("\r\n"), "\n");
    require(expected == actual, "Axis constants differ from manifest bindings");
    if (interface == "workbench-axis-v2")
      require(b.manifest.value("AxisExportMarker", Json{}) ==
                  "workbench_axis_v2",
              "V2 axis export marker missing");
  }
  if (has("CommunicationInterfaceVersion")) {
    require(std::find(b.manifest.at("NativeWorkerTargets").begin(),
                      b.manifest.at("NativeWorkerTargets").end(),
                      "st-communication-worker") !=
                b.manifest.at("NativeWorkerTargets").end(),
            "Native communication target missing");
    require(b.manifest.at("CommunicationInterfaceVersion") ==
                "workbench-communication-v1",
            "Unsupported communication interface");
    for (auto n : {"communication-manifest.json", "communication-constants.st",
                   "communication_lifecycle.inc", "communication_bridge.cpp",
                   "communication_bridge.hpp"})
      require(b.files.contains(n),
              "Incomplete communication dependency bundle");
    auto data = parse_json(b.files.at("communication-manifest.json"));
    stcommunication::validate(data);
    std::string expected = "VAR_GLOBAL CONSTANT\n";
    for (auto &c : data.at("Channels"))
      expected +=
          "    COMM_" + c.at("Configuration").at("Name").get<std::string>() +
          " : COMM_REF := " + std::to_string(c.at("Index").get<int>()) + ";\n";
    expected += "END_VAR\n";
    require(std::regex_replace(b.files.at("communication-constants.st"),
                               std::regex("\r\n"), "\n") == expected,
            "Communication constants differ from manifest");
    if (b.files.contains("hardware-configuration.json")) {
      auto h = parse_json(b.files.at("hardware-configuration.json"));
      auto cs = h.at("Communication").at("Channels");
      std::sort(cs.begin(), cs.end(), [](auto &a, auto &b) {
        return a.at("Name").template get<std::string>() <
               b.at("Name").template get<std::string>();
      });
      Json expected = Json::array();
      for (auto &c : data.at("Channels"))
        expected.push_back(c.at("Configuration"));
      require(cs == expected, "Communication hardware differs from manifest");
    }
  }
  if (has("MotionInterfaceVersion")) {
    require(std::find(b.manifest.at("NativeWorkerTargets").begin(),
                      b.manifest.at("NativeWorkerTargets").end(),
                      "st-path-worker") !=
                b.manifest.at("NativeWorkerTargets").end(),
            "Native continuous target missing");
    require(b.manifest.at("MotionInterfaceVersion") ==
                "workbench-continuous-v1",
            "Unsupported continuous interface");
    for (auto n : {"paths/motion-manifest.json", "runtime_lifecycle.inc",
                   "continuous_path.st", "motion_bridge.cpp",
                   "motion_bridge.hpp", "generated_scan_shim.cpp"})
      require(b.files.contains(n), "Incomplete continuous dependency bundle");
    auto d = parse_json(b.files.at("paths/motion-manifest.json"));
    require(d.is_object() &&
                d.value("Backend", Json{}) == "VirtualContinuous" &&
                d.value("Paths", Json{}).is_array(),
            "Invalid virtual path manifest");
  }
}
inline Json hardware_bundle(const fs::path &version) {
  auto b = staged(version);
  Contents required;
  for (auto n :
       {"ethercat-runtime.json", "machine-config.json", "io-map.json"}) {
    require(b.files.contains(n), "Hardware bundle incomplete");
    required[n] = b.files.at(n);
  }
  auto f = topology(required);
  require(b.manifest.value("EtherCatTopologyFingerprint", Json{}) == f,
          "Build topology identity differs");
  return {{"status", "validated"},
          {"buildHash", lower(identity(version.filename().string()))},
          {"topologyFingerprint", f},
          {"machineSha256", sha256(required.at("machine-config.json"))},
          {"ioMapSha256", sha256(required.at("io-map.json"))}};
}
inline void matching(const Json &a, const Json &b) {
  for (auto n :
       {"buildHash", "topologyFingerprint", "machineSha256", "ioMapSha256"})
    require(identity(a.value(n, Json{})) == identity(b.value(n, Json{})),
            "Applied hardware bundle identity differs");
}
inline Json verify_applied(const fs::path &version) {
  auto r = hardware_bundle(version);
  auto h = native_state();
  require(h.value("ethercatTopologyFingerprint", Json{}) ==
              r.at("topologyFingerprint"),
          "Loaded native hardware identity differs");
  auto b = staged(version);
  hardware::loaded(
      h, hardware::requirements(parse_json(b.files.at("machine-config.json")),
                                b.manifest));
  r["status"] = "verified";
  r["verified"] = true;
  return r;
}
inline Json capability(const Channel &c) {
  require(c.endpoint.starts_with('/') && c.endpoint.size() < 104 &&
              c.endpoint.find('\0') == c.endpoint.npos,
          "Private absolute worker socket required");
  struct stat s{};
  require(lstat(c.endpoint.c_str(), &s) == 0 && S_ISSOCK(s.st_mode) &&
              s.st_uid == getuid() && (s.st_mode & 0777) == 0600,
          "Worker socket must be private owned mode0600");
  auto p = fs::path(c.endpoint + ".capabilities.json");
  auto raw = read_file(p, 65536, true);
  require(stat(p.c_str(), &s) == 0 && std::time(nullptr) - s.st_mtime <= 5 &&
              std::time(nullptr) - s.st_mtime >= -1,
          "Worker metadata stale or future");
  auto j = parse_json(raw);
  auto pid = j.value("PID", Json{});
  require(hardware::integer(pid) && pid > 0 && pid <= INT32_MAX,
          "Invalid worker PID");
  require(kill(pid.get<int>(), 0) == 0 &&
              stat(("/proc/" + std::to_string(pid.get<int>())).c_str(), &s) ==
                  0 &&
              s.st_uid == getuid(),
          "Worker process owner differs");
  return j;
}
inline std::string runtime_environment() {
  return process({"/usr/bin/systemctl", "--user", "show",
                  "stworkbench-openplc.service", "--property=Environment",
                  "--value"},
                 {}, 10)
      .output;
}
inline void verify_environment(const Channel &c,
                               const std::string &environment) {
  // Values used by these endpoints contain no shell escapes/quotes; tolerate
  // systemd's quoted tokens.
  auto tokens = environment;
  tokens = std::regex_replace(tokens, std::regex("\""), "");
  std::istringstream in(tokens);
  std::string token;
  std::map<std::string, std::string> values;
  while (in >> token) {
    auto p = token.find('=');
    if (p == token.npos)
      continue;
    require(values.emplace(token.substr(0, p), token.substr(p + 1)).second,
            "Duplicate Runtime environment variable");
  }
  require(values[c.environment] == c.endpoint,
          "Runtime worker socket environment differs");
  if (c.target == "st-axis-worker")
    require(values["STWORKBENCH_AXIS_BACKEND"] == c.backend,
            "Runtime axis backend differs");
  if (c.target == "st-path-worker")
    require(values["STWORKBENCH_MOTION_BACKEND"] == c.backend,
            "Runtime continuous backend differs");
}
inline Json native_receipt(const fs::path &version, const Bundle &b,
                           const Channel &c, bool installed) {
  auto receipt = read_json(version / "native-build-receipt.json");
  require(receipt.value("source_bundle_sha256", Json{}) ==
              b.manifest.at("NativeSourceBundleSha256"),
          "Native compiled source identity differs");
  auto t = receipt.at("targets").at(c.target);
  auto path = fs::path(t.at("path").get<std::string>());
  require(path == version / "native-build" / c.target &&
              file_hash(path) ==
                  lower(identity(t.at("elf_sha256").get<std::string>())),
          "Compiled worker ELF differs");
  if (installed) {
    auto dir = fs::path(c.endpoint).parent_path();
    auto record = read_json(dir / "worker-receipt.json");
    require(record.value("schemaVersion", Json{}) == 1 &&
                record.value("source_bundle_sha256", Json{}) ==
                    receipt.at("source_bundle_sha256") &&
                record.value("elf_sha256", Json{}) ==
                    t.at("elf_sha256").get<std::string>() &&
                record.value("target", Json{}) == c.target,
            "Installed worker receipt differs");
    auto installed = dir / c.target;
    require(file_hash(installed) == t.at("elf_sha256").get<std::string>(),
            "Installed worker ELF differs");
    auto cap = capability(c);
    auto proc =
        fs::path("/proc") / std::to_string(cap.at("PID").get<int>()) / "exe";
    require(fs::read_symlink(proc) == installed,
            "Running worker executable differs");
    struct stat live{}, disk{};
    require(stat(proc.c_str(), &live) == 0 &&
                stat(installed.c_str(), &disk) == 0 &&
                live.st_dev == disk.st_dev && live.st_ino == disk.st_ino,
            "Running worker ELF inode differs");
  }
  return t;
}
inline Json verify_worker(const fs::path &version, const Json &config,
                          const std::string &interface, bool installed = true) {
  auto b = staged(version);
  auto c = channel(config, interface);
  verify_environment(c, runtime_environment());
  native_receipt(version, b, c, installed);
  auto cap = capability(c);
  Json expected = {{"SchemaVersion", 1},
                   {"ManifestSha256", sha256(b.files.at(c.manifest))}};
  if (c.target == "st-axis-worker") {
    auto d = parse_json(b.files.at("axis-manifest.json"));
    require(c.backend == "EcMotion" || c.backend == "EcMotionSimulator",
            "Basic axis backend not explicitly configured");
    for (auto &a : d.at("Axes"))
      require(a.value("Mechanics", Json{}).is_null(),
              "Offline bridge mechanics cannot apply");
    expected.update({{"InterfaceVersion", interface},
                     {"Backend", c.backend},
                     {"ControllerReady", true},
                     {"PhysicalBasicAxis", c.backend == "EcMotion"},
                     {"SimulatorBasicAxis", c.backend == "EcMotionSimulator"},
                     {"Continuous", false},
                     {"MotionCycleApplied", false}});
    if (interface == "workbench-axis-v2") {
      expected.update({{"CommandBytes", 4112},
                       {"FeedbackBytes", 6656},
                       {"RequiredRuntimeCapabilities",
                        d.at("RequiredRuntimeCapabilities")}});
      std::set<std::string> required;
      for (auto &v : d.at("RequiredRuntimeCapabilities"))
        required.insert(v.get<std::string>());
      hardware::loaded(native_state(), required);
      auto caps = cap.at("NativeRuntimeCapabilities");
      for (auto &r : required)
        require(std::find(caps.begin(), caps.end(), r) != caps.end(),
                "Worker native dynamic capability differs");
    }
  } else if (c.target == "st-communication-worker") {
    require(config.value("communication_backend", Json{}) == "NativeClient",
            "Communication backend not configured");
    expected.update({{"InterfaceVersion", "workbench-communication-v1"},
                     {"WorkerReady", true}});
    require(hardware::integer(cap.value("LastScan", Json{})) &&
                cap.value("ScanHealthy", Json{}).is_boolean(),
            "Invalid communication scan capability");
  } else {
    require(config.value("motion_backend", Json{}) == "VirtualContinuous",
            "Physical continuous backend unsupported");
    expected.update(
        {{"Backend", "VirtualContinuous"}, {"PhysicalContinuous", false}});
    Json paths = Json::array();
    for (auto &p : parse_json(b.files.at(c.manifest)).at("Paths")) {
      Json x;
      for (auto key : {"Id", "NumericId", "Version", "ContentHash", "Axes"})
        x[key] = p.at("Asset").at(key);
      paths.push_back(x);
    }
    expected["Paths"] = paths;
  }
  for (auto i = expected.begin(); i != expected.end(); ++i)
    require(cap.contains(i.key()) &&
                (cap[i.key()].type() == i.value().type() ||
                 (hardware::integer(cap[i.key()]) &&
                  hardware::integer(i.value()))) &&
                cap[i.key()] == i.value(),
            "Worker capability or candidate identity differs: " + i.key());
  return cap;
}
inline void stationary(const fs::path &version) {
  verify_applied(version);
  auto b = staged(version);
  diagnostics::StationaryProof proof(
      parse_json(b.files.at("machine-config.json")));
  auto until = monotonic_ns() + proof.duration_ns() + 2000000000LL;
  while (monotonic_ns() < until) {
    auto s = snapshot();
    for (auto &a : s.at("axes"))
      require(a.value("state", Json{}) == "Disabled" && a.contains("fault") &&
                  a["fault"].is_null(),
              "Every native axis must be Disabled and fault-free");
    if (proof.observe(s, monotonic_ns()))
      return;
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
  }
  throw std::runtime_error("Stationary dwell unconfirmed");
}
} // namespace stworkbench::deployment

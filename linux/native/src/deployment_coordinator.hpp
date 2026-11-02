// SPDX-License-Identifier: MIT
#pragma once
#include "deployment_admission.hpp"
#include "deployment_realtime.hpp"
#include "stworkbench/axis_recovery.hpp"
#include <fstream>
namespace stworkbench::deployment {
inline std::string base64_decode(const std::string &text) {
  static const std::string alphabet =
      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  require(text.size() <= 32 * 1024 * 1024 * 4 / 3 + 4 && text.size() % 4 == 0,
          "Invalid package base64 length");
  std::string out;
  out.reserve(text.size() / 4 * 3);
  for (std::size_t i = 0; i < text.size(); i += 4) {
    uint32_t value = 0;
    int padding = 0;
    for (int k = 0; k < 4; ++k) {
      auto c = text[i + k];
      if (c == '=') {
        require(k >= 2 && i + 4 == text.size(),
                "Invalid package base64 padding");
        padding++;
        value <<= 6;
      } else {
        require(!padding, "Invalid base64 padding sequence");
        auto at = alphabet.find(c);
        require(at != alphabet.npos, "Invalid package base64 character");
        value = (value << 6) | at;
      }
    }
    require(padding <= 2 && (padding != 1 || (value & 255) == 0) &&
                (padding != 2 || (value & 65535) == 0),
            "Noncanonical package base64");
    out += char(value >> 16);
    if (padding < 2)
      out += char(value >> 8);
    if (!padding)
      out += char(value);
  }
  require(out.size() <= 32 * 1024 * 1024, "Package capacity exceeded");
  return out;
}
inline Json compiled_candidate(const fs::path &v) {
  auto b = staged(v);
  require(b.manifest.value("ArtifactKind", Json{}) != "hardware-only",
          "Standalone hardware cannot become application");
  auto c = read_json(v / "compile-result.json");
  require(c.value("state", Json{}) == "compiled" &&
              c.value("exports_verified", Json{}) == true &&
              identity(c.at("build_hash")) == identity(v.filename().string()) &&
              file_hash(v / "build/new_libplc.so") ==
                  c.at("library_sha256").get<std::string>(),
          "Candidate has no verified successful compilation");
  return c;
}
inline std::string pinned_source(std::string relative, std::string content) {
  content = std::regex_replace(content, std::regex("\r\n"), "\n");
  if (relative == "core/src/plc_app/task_policy.h") {
    std::string source = "/home/a/.local/share/st-workbench/runtime-v4.3.0/run/"
                         "watchdog_fault",
                replacement = "/run/runtime/watchdog_fault";
    auto at = content.find(source);
    if (at != content.npos && content.find(source, at + 1) == content.npos)
      content.replace(at, source.size(), replacement);
  }
  return content;
}
inline std::string candidate_source(const std::string &relative,
                                    std::string content, const Json &m) {
  if (relative != "core/strucpp_runtime/runtime_v4_entry.cpp")
    return content;
  std::string includes, calls;
  for (auto [key, file, function] :
       std::vector<std::tuple<std::string, std::string, std::string>>{
           {"MotionInterfaceVersion", "runtime_lifecycle.inc",
            "workbench_motion_bootstrap"},
           {"AxisInterfaceVersion", "axis_lifecycle.inc",
            "workbench_axis_bootstrap"},
           {"CommunicationInterfaceVersion", "communication_lifecycle.inc",
            "workbench_comm_bootstrap"}}) {
    if (!m.value(key, Json{}).is_null()) {
      includes += "#include \"" + file + "\"\n";
      calls += "    if (!" + function + "()) return nullptr;\n";
    }
  }
  if (calls.empty())
    return content;
  std::string entry = "extern \"C\" strucpp::ConfigurationInstance* "
                      "strucpp_get_config(void) {\n    return &g_config;\n}";
  auto at = content.find(entry);
  require(at != content.npos && content.find(entry, at + 1) == content.npos,
          "Pinned Runtime bootstrap location changed");
  auto hook = includes + entry;
  hook.replace(hook.find("    return &g_config;"), 0, calls);
  content.replace(at, entry.size(), hook);
  return content;
}
class Coordinator {
public:
  Json config;
  fs::path prefix, runtime, active_path, transaction_path, profile_path;
  explicit Coordinator(
      Json c,
      fs::path profile = "/home/a/st-workbench-deployment/runtime-profile.json")
      : config(std::move(c)), prefix(config.at("prefix").get<std::string>()),
        runtime(config.at("runtime_source").get<std::string>()),
        active_path(prefix / "active.json"),
        transaction_path(prefix / "transaction.json"),
        profile_path(std::move(profile)) {
    require(config.value("runtime_commit", Json{}) == RuntimeCommit &&
                config.value("exclusive_control", Json{}) == true &&
                config.value("control_transport", Json{}) == "rest",
            "Pinned private Runtime with exclusive REST control required");
    require(prefix.is_absolute() && runtime.is_absolute(),
            "Absolute Runtime paths required");
    fs::create_directories(prefix);
  }
  fs::path version(const Json &build) const {
    return prefix / ".versions" / identity(build);
  }
  Json active() { return read_json(active_path); }
  fs::path worker_version(const Json &a) {
    auto v = version(a.at("build_hash"));
    auto b = staged(v);
    if (!b.manifest.value("NativeWorkerInterfaceVersion", Json{}).is_null())
      return v;
    auto binding = read_json(prefix / "legacy-native-migration.json");
    require(binding.value("schemaVersion", Json{}) == 1 &&
                binding.value("state", Json{}) == "committed" &&
                binding.value("verified", Json{}) == true &&
                binding.at("legacy_active") == a,
            "Legacy program requires verified native migration binding");
    auto source = version(binding.at("native_build_hash"));
    auto replacement = staged(source);
    require(binding.at("source_bundle_sha256") ==
                replacement.manifest.at("NativeSourceBundleSha256"),
            "Legacy native source provenance changed");
    require(binding.at("native_targets") ==
                read_json(source / "native-build-receipt.json").at("targets"),
            "Legacy compiled ELF provenance changed");
    for (auto key : {"AxisInterfaceVersion", "CommunicationInterfaceVersion",
                     "MotionInterfaceVersion"}) {
      require(b.manifest.value(key, Json{}) ==
                  replacement.manifest.value(key, Json{}),
              "Legacy/native worker ABI differs");
      if (!b.manifest.value(key, Json{}).is_null()) {
        auto c = channel(config, b.manifest.at(key));
        require(b.files.at(c.manifest) == replacement.files.at(c.manifest),
                "Legacy/native manifest bytes differ");
      }
    }
    return source;
  }
  std::vector<fs::path> libraries() {
    std::vector<fs::path> out;
    for (auto &e : fs::directory_iterator(runtime / "build")) {
      auto n = e.path().filename().string();
      if (n.starts_with("libplc_") && n.find(".so") != n.npos)
        out.push_back(e.path());
    }
    return out;
  }
  void verify_active(const Json &a, const Json &expected) {
    require(a.value("build_hash", Json{}) == expected,
            "Active identity changed; refresh before retry");
    auto libs = libraries();
    if (a.value("build_hash", Json{}).is_null())
      require(libs.empty(),
              "Empty baseline contains unexpected program libraries");
    else
      require(
          libs.size() == 1 &&
              libs[0] == fs::path(a.at("library_path").get<std::string>()) &&
              file_hash(libs[0]) == a.at("library_sha256").get<std::string>(),
          "Active library bytes/path differ from adopted identity");
  }
  void stopped(bool empty = false) {
    Runtime r(config);
    auto state = r.status();
    require(state == "STOPPED" || (empty && state == "EMPTY"),
            "Activation requires confirmed PLC STOPPED");
  }
  void service(const std::string &action) {
    require(config.value("service_name", Json{}) ==
                    "stworkbench-openplc.service" &&
                config.value("safe_mode_verified", Json{}) == true,
            "Exact private safe-mode service required");
    auto properties =
        process({"/usr/bin/systemctl", "--user", "show",
                 "stworkbench-openplc.service", "--property=KillMode"})
            .output;
    require(properties == "KillMode=control-group\n",
            "Service must terminate complete process group");
    user_service(action, "stworkbench-openplc.service");
  }
  void resume(bool empty = false) {
    service("start");
    auto until = monotonic_ns() + 30000000000LL;
    std::string error;
    while (monotonic_ns() < until) {
      try {
        stopped(empty);
        return;
      } catch (const std::exception &e) {
        error = e.what();
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(250));
    }
    throw std::runtime_error(
        "Restarted service state unknown; PLC was never requested to START: " +
        error);
  }
  static int64_t core_pid() {
    auto r = process({"/usr/bin/systemctl", "show",
                      "st-workbench-is620n.service", "--property=LoadState",
                      "--property=ActiveState", "--property=MainPID"},
                     {}, 10, 8192, false);
    std::map<std::string, std::string> m;
    std::istringstream in(r.output);
    std::string line;
    while (std::getline(in, line)) {
      auto p = line.find('=');
      require(p != line.npos &&
                  m.emplace(line.substr(0, p), line.substr(p + 1)).second,
              "Native service state unconfirmed");
    }
    require(
        m.size() == 3 && m.contains("LoadState") && m.contains("ActiveState") &&
            m.contains("MainPID") && !m["MainPID"].empty() &&
            m["MainPID"].find_first_not_of("0123456789") == std::string::npos,
        "Native service state unconfirmed");
    auto pid = std::stoll(m["MainPID"]);
    if (m["LoadState"] == "not-found" && m["ActiveState"] == "inactive" && !pid)
      return 0;
    require(r.exit_code == 0 && m["LoadState"] == "loaded" &&
                !(pid && (m["ActiveState"] == "inactive" ||
                          m["ActiveState"] == "failed")),
            "Native service state unconfirmed");
    return pid;
  }
  void verify_start_hardware(const Json &a) {
    const auto record = fs::path(config.value(
        "ethercat_active_record", (prefix / "hardware-active.json").string()));
    if (!a.value("build_hash", Json{}).is_string()) {
      require(!fs::exists(record), "Empty baseline has applied hardware");
      return;
    }
    auto build = a["build_hash"].get<std::string>();
    if (!std::regex_match(build, std::regex("[A-Fa-f0-9]{64}"))) {
      require(!fs::exists(record), "Legacy baseline cannot run against a newer "
                                   "applied hardware identity");
      return;
    }
    auto v = version(build);
    auto b = staged(v);
    if (fs::exists(record)) {
      auto actual = read_json(record);
      require(b.manifest.value("EtherCatRuntimeInterfaceVersion", Json{}) ==
                      "workbench-ethercat-v1" &&
                  b.manifest.value("EtherCatTopologyFingerprint", Json{}) ==
                      actual.value("topologyFingerprint", Json{}),
              "Active PLC hardware configuration is stale");
    }
    if (!b.manifest.value("EtherCatRuntimeInterfaceVersion", Json{}).is_null())
      verify_applied(v);
    bool uses = false;
    for (auto key : {"AxisInterfaceVersion", "CommunicationInterfaceVersion",
                     "MotionInterfaceVersion"})
      uses |= !b.manifest.value(key, Json{}).is_null();
    if (uses)
      verify_workers(worker_version(a));
    else
      verify_workers(v);
  }
  void verify_workers(const fs::path &v) {
    auto b = staged(v);
    if (!b.manifest.value("MotionCycleInterfaceVersion", Json{}).is_null())
      throw std::runtime_error(
          "Physical EcMotion execution adapter is unavailable; motion-cycle "
          "activation is not supported");
    for (auto key : {"AxisInterfaceVersion", "CommunicationInterfaceVersion",
                     "MotionInterfaceVersion"})
      if (!b.manifest.value(key, Json{}).is_null())
        verify_worker(v, config, b.manifest.at(key));
  }
  bool hardware_matches(const Json &a) {
    try {
      verify_start_hardware(a);
      return true;
    } catch (...) {
      return false;
    }
  }
  Json stage(const Json &request, bool hardware_only = false) {
    auto build = identity(request.at("build_hash"));
    TempDirectory input(prefix / "incoming");
    fs::path package;
    if (request.contains("package_base64")) {
      require(request["package_base64"].is_string(), "Invalid base64 package");
      package = input.path / "candidate.zip";
      atomic_write(package, base64_decode(request["package_base64"]));
    } else {
      require(request.contains("package_path") &&
                  request["package_path"].is_string(),
              "Package path missing");
      package = request["package_path"].get<std::string>();
    }
    auto b = read_bundle(package, request.at("package_sha256"), build,
                         hardware_only);
    if (!hardware_only)
      validate_dependencies(b);
    else {
      auto a = parse_json(b.files.at("axis-manifest.json"));
      if (!a.at("Axes").empty())
        axis::Manifest::from(a, sha256(b.files.at("axis-manifest.json")));
    }
    auto v = version(build);
    if (fs::exists(v)) {
      auto old = staged(v);
      require(old.manifest == b.manifest && old.files == b.files,
              "Build identity already staged with different content");
      return {{"state", "staged"}, {"build_hash", build}};
    }
    fs::create_directories(v.parent_path());
    TempDirectory temp(v.parent_path());
    auto generated = temp.path / "core/generated";
    for (auto &[name, body] : b.files) {
      fs::create_directories((generated / name).parent_path());
      atomic_write(generated / name, body);
    }
    save_json(
        temp.path / "stage-result.json",
        {{"state", "staged"},
         {"build_hash", build},
         {"package_sha256", lower(identity(request.at("package_sha256")))},
         {"manifest", b.manifest},
         {"request_id", request.at("request_id")}});
    fs::rename(temp.path, v);
    return {{"state", "staged"}, {"build_hash", build}};
  }
  Json compile(const Json &request) {
    auto v = version(request.at("build_hash"));
    auto b = staged(v);
    require(b.manifest.value("ArtifactKind", Json{}) != "hardware-only",
            "Standalone hardware cannot enter program compilation");
    validate_dependencies(b);
    auto profile = read_json(profile_path);
    for (auto i = profile.at("source_hashes").begin();
         i != profile.at("source_hashes").end(); ++i) {
      safe_member(i.key());
      auto raw = read_file(runtime / i.key(), 16 * 1024 * 1024);
      raw = std::regex_replace(raw, std::regex("\r\n"), "\n");
      require(identity(sha256(pinned_source(i.key(), raw))) ==
                  identity(i.value()),
              "Pinned Runtime compile source changed: " + i.key());
      auto target = v / i.key();
      fs::create_directories(target.parent_path());
      atomic_write(target, candidate_source(i.key(), raw, b.manifest));
    }
    auto log =
        process({"/usr/bin/make", "-j1", "-f", "scripts/Makefile.strucpp"}, v,
                300, 1024 * 1024, false);
    atomic_write(v / "compile.log", log.output);
    require(log.exit_code == 0 && fs::exists(v / "build/new_libplc.so"),
            "Candidate compilation failed; inspect compile.log");
    auto nm = process({"/usr/bin/nm", "-D", "--defined-only",
                       (v / "build/new_libplc.so").string()},
                      v, 15);
    atomic_write(v / "exports.txt", nm.output);
    std::set<std::string> exports;
    std::istringstream in(nm.output);
    std::string line;
    while (std::getline(in, line)) {
      std::istringstream row(line);
      std::string token, last;
      while (row >> token)
        last = token;
      if (!last.empty())
        exports.insert(last);
    }
    std::set<std::string> required = {"strucpp_get_config",
                                      "strucpp_get_located_vars",
                                      "strucpp_get_located_var_count",
                                      "strucpp_advance_time",
                                      "strucpp_set_current_time",
                                      "strucpp_program_md5",
                                      "strucpp_debug_array_count",
                                      "strucpp_debug_elem_count",
                                      "strucpp_debug_size",
                                      "strucpp_debug_set",
                                      "strucpp_debug_read",
                                      "strucpp_debug_write"};
    if (!b.manifest.value("TaskSchedulingInterfaceVersion", Json{}).is_null())
      required.insert("workbench_task_scheduling_v1");
    if (b.manifest.value("AxisInterfaceVersion", Json{}) == "workbench-axis-v2")
      required.insert("workbench_axis_v2");
    for (auto &name : required)
      require(exports.contains(name),
              "Candidate missing pinned export: " + name);
    Json result = {{"state", "compiled"},
                   {"build_hash", identity(request.at("build_hash"))},
                   {"library_sha256", file_hash(v / "build/new_libplc.so")},
                   {"exports_verified", true},
                   {"runtime_commit", RuntimeCommit},
                   {"log_path", (v / "compile.log").string()},
                   {"request_id", request.at("request_id")},
                   {"log", log.output.substr(log.output.size() > 65536
                                                 ? log.output.size() - 65536
                                                 : 0)}};
    if (b.manifest.contains("NativeWorkerTargets")) {
      auto source_hash = native_sources(b.manifest, b.files);
      auto native = v / "core/generated/native";
      auto output = v / "native-build";
      process({"/usr/bin/cmake", "-S", native.string(), "-B", output.string(),
               "-DBUILD_TESTING=OFF", "-DCMAKE_BUILD_TYPE=Release"},
              v, 60);
      Json receipt = {
          {"schemaVersion", 1},
          {"source_bundle_sha256", source_hash},
          {"targets", Json::object()},
          {"compiler", process({"/usr/bin/c++", "--version"}).output}};
      bool clean = true;
      for (auto &t : b.manifest.at("NativeWorkerTargets")) {
        auto target = t.get<std::string>();
        std::vector<std::string> command = {"/usr/bin/cmake", "--build",
                                            output.string(),  "--target",
                                            target,           "-j2"};
        if (clean)
          command.push_back("--clean-first");
        process(command, v, 300);
        clean = false;
        auto elf = output / target;
        receipt["targets"][target] = {{"path", elf.string()},
                                      {"elf_sha256", file_hash(elf)}};
      }
      save_json(v / "native-build-receipt.json", receipt);
    }
    for (auto [key, ready, message] :
         std::vector<std::tuple<std::string, std::string, std::string>>{
             {"AxisInterfaceVersion", "axis_ready", "axis_message"},
             {"CommunicationInterfaceVersion", "communication_ready",
              "communication_message"},
             {"MotionInterfaceVersion", "motion_ready", "motion_message"}})
      if (!b.manifest.value(key, Json{}).is_null()) {
        result[ready] = false;
        try {
          verify_worker(v, config, b.manifest.at(key));
          result[ready] = true;
          result[message] =
              "Verified native worker source, ELF and candidate identity";
        } catch (const std::exception &e) {
          result[message] = e.what();
        }
      }
    save_json(v / "compile-result.json", result);
    return result;
  }
  Json plan(const fs::path &v) {
    compiled_candidate(v);
    auto b = staged(v);
    auto checked = hardware_bundle(v);
    auto hello = native_state();
    std::string kind = hello.at("ethercatTopologyFingerprint") ==
                               checked.at("topologyFingerprint")
                           ? "none"
                           : "hardware";
    Json axis_hash, loaded_hash;
    if (!b.manifest.value("AxisInterfaceVersion", Json{}).is_null()) {
      axis_hash = sha256(b.files.at("axis-manifest.json"));
      auto c = channel(config, b.manifest.at("AxisInterfaceVersion"));
      verify_environment(c, runtime_environment());
      if (kind == "none") {
        try {
          loaded_hash = capability(c).at("ManifestSha256");
        } catch (...) {
          require(
              !fs::exists(c.endpoint),
              "Existing axis channel is invalid; explicit recovery required");
        }
        bool source_matches = false;
        try {
          native_receipt(v, b, c, true);
          source_matches = true;
        } catch (...) {
        }
        if (loaded_hash != axis_hash || !source_matches)
          kind = "axis";
      }
    }
    Json result = {{"build_hash", identity(v.filename().string())},
                   {"kind", kind},
                   {"topology_fingerprint", checked.at("topologyFingerprint")},
                   {"axis_manifest_sha256", axis_hash},
                   {"machine_sha256", checked.at("machineSha256")},
                   {"io_map_sha256", checked.at("ioMapSha256")},
                   {"loaded_topology_fingerprint",
                    hello.at("ethercatTopologyFingerprint")},
                   {"loaded_axis_manifest_sha256", loaded_hash}};
    result["identity"] = sha256(result.dump());
    return result;
  }
  void install_worker(const fs::path &v, const std::string &interface) {
    auto b = staged(v);
    auto c = channel(config, interface);
    auto t = native_receipt(v, b, c, false);
    auto dir = fs::path(c.endpoint).parent_path();
    fs::create_directories(dir);
    require(chmod(dir.c_str(), 0700) == 0,
            "Worker directory permission failed");
    atomic_write(dir / c.target,
                 read_file(t.at("path").get<std::string>(), 64 * 1024 * 1024),
                 0700);
    auto dest = c.target == "st-path-worker" ? dir / "motion-manifest.json"
                                             : dir / c.manifest;
    atomic_write(dest, b.files.at(c.manifest));
    save_json(
        dir / "worker-receipt.json",
        {{"schemaVersion", 1},
         {"source_bundle_sha256", b.manifest.at("NativeSourceBundleSha256")},
         {"elf_sha256", t.at("elf_sha256").get<std::string>()},
         {"target", c.target},
         {"build_hash", identity(v.filename().string())}});
    auto quote = [](const std::string &value) {
      require(value.find_first_of("\r\n") == value.npos &&
                  value.find('\0') == value.npos,
              "Unsafe service argument");
      std::string out = "\"";
      for (char x : value) {
        if (x == '%')
          out += "%%";
        else {
          if (x == '\\' || x == '\"')
            out += '\\';
          out += x;
        }
      }
      return out + '\"';
    };
    auto unit_root = fs::path("/home/a/.config/systemd/user");
    fs::create_directories(unit_root);
    std::string command = quote((dir / c.target).string()) + " --manifest " +
                          quote(dest.string()) + " --socket " +
                          quote(c.endpoint);
    if (c.target == "st-axis-worker")
      command += " --controller-socket " +
                 quote(config.value(
                     "ethercat_controller_socket",
                     std::string("/run/st-workbench-is620n/controller.sock"))) +
                 " --backend " + quote(c.backend);
    auto unit = "[Unit]\nDescription=Workbench native " + c.target +
                "\n[Service]\nType=simple\nUMask=0077\nExecStart=" + command +
                "\nRestart=no\nKillMode=control-group\nTimeoutStopSec=10\n["
                "Install]\nWantedBy=default.target\n";
    atomic_write(unit_root / c.unit, unit, 0600);
    process({"/usr/bin/systemctl", "--user", "daemon-reload"});
  }
  void stop_workers() {
    for (auto unit :
         {"stworkbench-is620n-axis.service", "stworkbench-axis-v2.service",
          "stworkbench-communication.service", "stworkbench-motion.service"}) {
      auto loaded = process({"/usr/bin/systemctl", "--user", "show", unit,
                             "--property=LoadState", "--value"},
                            {}, 10, 8192, false);
      if (loaded.output == "loaded\n")
        process({"/usr/bin/systemctl", "--user", "--no-block", "stop", unit},
                {}, 10, 8192, false);
    }
  }
  void start_active_workers() {
    if (fs::exists(transaction_path)) {
      auto state = read_json(transaction_path).value("state", Json{});
      require(state == "committed" || state == "rolled-back",
              "Unfinished deployment blocks worker lifecycle startup");
    }
    if (!fs::exists(active_path))
      return;
    auto a = active();
    verify_active(a, a.value("build_hash", Json{}));
    if (a.value("build_hash", Json{}).is_null())
      return;
    auto legacy = staged(version(a.at("build_hash")));
    bool uses = false;
    for (auto key : {"AxisInterfaceVersion", "CommunicationInterfaceVersion",
                     "MotionInterfaceVersion"})
      uses |= !legacy.manifest.value(key, Json{}).is_null();
    if (!uses)
      return;
    auto v = worker_version(a);
    auto b = staged(v);
    for (auto key : {"AxisInterfaceVersion", "CommunicationInterfaceVersion",
                     "MotionInterfaceVersion"})
      if (!b.manifest.value(key, Json{}).is_null()) {
        auto c = channel(config, b.manifest.at(key));
        native_receipt(v, b, c, false);
        auto dir = fs::path(c.endpoint).parent_path();
        auto receipt = read_json(dir / "worker-receipt.json");
        require(receipt.at("source_bundle_sha256") ==
                        b.manifest.at("NativeSourceBundleSha256") &&
                    receipt.at("elf_sha256").get<std::string>() ==
                        file_hash(dir / c.target),
                "Active worker executable/receipt differs");
        auto dest = c.target == "st-path-worker" ? dir / "motion-manifest.json"
                                                 : dir / c.manifest;
        require(read_file(dest, 16 * 1024 * 1024) == b.files.at(c.manifest),
                "Active installed worker manifest differs");
        user_service("start", c.unit);
      }
  }
  Json bind_legacy(const std::string &native_build) {
    stopped();
    auto a = active();
    verify_active(a, a.value("build_hash", Json{}));
    auto old = version(a.at("build_hash"));
    auto b = staged(old);
    require(b.manifest.value("NativeWorkerInterfaceVersion", Json{}).is_null(),
            "Active program already uses native worker provenance");
    auto v = version(native_build);
    compiled_candidate(v);
    auto replacement = staged(v);
    native_sources(replacement.manifest, replacement.files);
    validate_dependencies(replacement);
    require(
        b.manifest.value("EtherCatTopologyFingerprint", Json{}) ==
            replacement.manifest.value("EtherCatTopologyFingerprint", Json{}),
        "Legacy/native topology differs");
    verify_applied(old);
    stationary(old);
    auto pid = core_pid();
    require(pid > 0, "Native core identity unavailable");
    std::vector<std::string> interfaces;
    for (auto key : {"AxisInterfaceVersion", "CommunicationInterfaceVersion",
                     "MotionInterfaceVersion"}) {
      require(b.manifest.value(key, Json{}) ==
                  replacement.manifest.value(key, Json{}),
              "Legacy/native worker ABI differs");
      if (!b.manifest.value(key, Json{}).is_null()) {
        auto c = channel(config, b.manifest.at(key));
        require(b.files.at(c.manifest) == replacement.files.at(c.manifest),
                "Legacy/native worker manifest differs");
        native_receipt(v, replacement, c, false);
        interfaces.push_back(b.manifest.at(key));
      }
    }
    require(!interfaces.empty(), "Legacy program has no worker resources");
    auto receipt_path = prefix / "legacy-native-migration.json";
    Json receipt = {{"schemaVersion", 1},
                    {"state", "prepared"},
                    {"legacy_active", a},
                    {"native_build_hash", identity(native_build)},
                    {"source_bundle_sha256",
                     replacement.manifest.at("NativeSourceBundleSha256")},
                    {"native_targets",
                     read_json(v / "native-build-receipt.json").at("targets")},
                    {"native_core_pid", pid}};
    save_json(receipt_path, receipt);
    for (auto &i : interfaces)
      stop_loaded_worker(channel(config, i).unit);
    require(core_pid() == pid, "Native core changed after bridge stop");
    stopped();
    stationary(old);
    require(active() == a, "Active program changed during migration");
    verify_active(a, a.at("build_hash"));
    for (auto &i : interfaces)
      install_worker(v, i);
    receipt["state"] = "installed";
    save_json(receipt_path, receipt);
    for (auto &i : interfaces)
      user_service("start", channel(config, i).unit);
    wait_ready(v);
    require(core_pid() == pid, "Native core changed during migration");
    verify_applied(old);
    stationary(old);
    stopped();
    verify_active(a, a.at("build_hash"));
    require(active() == a, "Active program changed during migration");
    receipt["state"] = "committed";
    receipt["verified"] = true;
    save_json(receipt_path, receipt);
    return {{"state", "legacy-native-bound"},
            {"active", a},
            {"native_build_hash", identity(native_build)},
            {"plc_state", "STOPPED"},
            {"motion_restarted", false}};
  }
  void wait_ready(const fs::path &v) {
    auto until = monotonic_ns() + 10000000000LL;
    std::string error;
    while (monotonic_ns() < until) {
      try {
        verify_workers(v);
        return;
      } catch (const std::exception &e) {
        error = e.what();
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    throw std::runtime_error("Configuration feedback unconfirmed: " + error);
  }
  void apply_axis(const fs::path &v) {
    auto b = staged(v);
    auto interface = b.manifest.at("AxisInterfaceVersion").get<std::string>();
    auto c = channel(config, interface);
    stationary(v);
    auto pid = core_pid();
    require(pid > 0, "Native core PID unconfirmed");
    stop_loaded_worker(c.unit);
    require(core_pid() == pid, "Native core changed after bridge stop");
    stationary(v);
    install_worker(v, interface);
    user_service("start", c.unit);
    wait_ready(v);
    require(core_pid() == pid, "Native core changed during axis update");
  }
  Json apply_hardware(const Json &r) {
    stopped();
    auto a = active();
    verify_active(a, r.value("expected_active_build_hash", Json{}));
    auto v = version(r.at("build_hash"));
    auto expected = hardware_bundle(v);
    auto validated =
        broker("validate", v.filename().string(),
               r.at("request_id").get<std::string>().substr(0, 78) + "-v");
    matching(validated, expected);
    require(validated.value("status", Json{}) == "validated",
            "Hardware broker validation unconfirmed");
    auto oldpid = core_pid();
    std::vector<std::string> stopped_units;
    try {
      for (auto unit :
           {"stworkbench-is620n-axis.service", "stworkbench-axis-v2.service"}) {
        if (stop_loaded_worker(unit))
          stopped_units.push_back(unit);
      }
      auto applied = broker("apply", v.filename().string(), r.at("request_id"));
      matching(applied, expected);
      require(applied.value("status", Json{}) == "applied" &&
                  applied.value("verified", Json{}) == true,
              "Hardware apply unconfirmed");
      auto b = staged(v);
      auto axes = parse_json(b.files.at("axis-manifest.json"));
      if (!axes.at("Axes").empty()) {
        auto interface = axes.at("InterfaceVersion").get<std::string>();
        auto c = channel(config, interface);
        if (b.manifest.value("ArtifactKind", Json{}) == "hardware-only") {
          auto dest = fs::path(c.endpoint).parent_path() / "axis-manifest.json";
          atomic_write(dest, b.files.at("axis-manifest.json"));
        } else
          install_worker(v, interface);
        user_service("start", c.unit);
        auto until = monotonic_ns() + 10000000000LL;
        bool ok = false;
        while (monotonic_ns() < until) {
          try {
            ok = capability(c).value("ManifestSha256", Json{}) ==
                 sha256(b.files.at("axis-manifest.json"));
            if (ok)
              break;
          } catch (...) {
          }
          std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        require(ok, "Loaded axis manifest differs from applied hardware");
      }
      verify_applied(v);
      stopped();
      require(active() == a,
              "Active PLC identity changed during hardware operation");
      save_json(prefix / "hardware-active.json", applied);
      return {
          {"state", "hardware-applied"}, {"active", a}, {"hardware", applied}};
    } catch (...) {
      if (oldpid > 0) {
        try {
          if (core_pid() == oldpid)
            for (auto &unit : stopped_units)
              user_service("start", unit);
        } catch (...) {
        }
      }
      throw;
    }
  }
  Json apply_configuration(const Json &r) {
    stopped();
    auto a = active();
    verify_active(a, r.value("expected_active_build_hash", Json{}));
    auto v = version(r.at("build_hash"));
    auto p = plan(v);
    require(p.at("identity") == r.at("configuration_identity"),
            "Configuration plan identity changed; query again");
    if (p.at("kind") == "hardware")
      apply_hardware(r);
    else if (p.at("kind") == "axis")
      apply_axis(v);
    auto b = staged(v);
    for (auto key : {"CommunicationInterfaceVersion", "MotionInterfaceVersion"})
      if (!b.manifest.value(key, Json{}).is_null()) {
        auto c = channel(config, b.manifest.at(key));
        stop_loaded_worker(c.unit);
        install_worker(v, b.manifest.at(key));
        user_service("start", c.unit);
      }
    wait_ready(v);
    stopped();
    verify_active(a, r.value("expected_active_build_hash", Json{}));
    require(active() == a, "Active PLC changed during configuration apply");
    auto compiled = compiled_candidate(v);
    if (b.manifest.contains("AxisInterfaceVersion"))
      compiled["axis_ready"] = true;
    if (b.manifest.contains("CommunicationInterfaceVersion"))
      compiled["communication_ready"] = true;
    if (b.manifest.contains("MotionInterfaceVersion"))
      compiled["motion_ready"] = true;
    save_json(v / "compile-result.json", compiled);
    return {{"state", "configuration-applied"},
            {"configuration_plan", p},
            {"active", a},
            {"axis_ready", true}};
  }
  Json activate(const Json &r) {
    auto a = active();
    verify_active(a, r.value("expected_active_build_hash", Json{}));
    auto v = version(r.at("build_hash"));
    auto compiled = compiled_candidate(v);
    auto b = staged(v);
    verify_workers(v);
    if (!b.manifest.value("EtherCatRuntimeInterfaceVersion", Json{}).is_null())
      verify_applied(v);
    Runtime connection(config);
    if (!b.manifest.value("TaskSchedulingInterfaceVersion", Json{}).is_null())
      require(connection.task_supported(),
              "Runtime lacks verified task scheduling v1");
    stopped(a.value("build_hash", Json{}).is_null());
    const auto target = fs::path(a.at("library_path").get<std::string>());
    require(target.parent_path() == runtime / "build" &&
                target.filename().string().starts_with("libplc_") &&
                target.filename().extension() == ".so",
            "Active target outside reviewed runtime build");
    fs::create_directories(prefix / ".history");
    auto backup =
        prefix / ".history" / (r.at("request_id").get<std::string>() + ".so");
    if (fs::exists(target))
      atomic_write(backup, read_file(target, 100 * 1024 * 1024));
    Json next = {
        {"build_hash", identity(r.at("build_hash"))},
        {"library_path", target.string()},
        {"library_sha256", compiled.at("library_sha256").get<std::string>()},
        {"program_md5", b.manifest.at("ProgramMd5")},
        {"symbol_table_hash", b.manifest.at("SymbolTableHash")},
        {"hardware_hash", b.manifest.at("HardwareHash")},
        {"source_hash", b.manifest.at("SourceHash")},
        {"runtime_commit", RuntimeCommit},
        {"request_id", r.at("request_id")}};
    Json transaction = {
        {"state", "prepared"},
        {"request_id", r.at("request_id")},
        {"old_active", a},
        {"new_active", next},
        {"backup_path", backup.string()},
        {"candidate_path", (v / "build/new_libplc.so").string()}};
    save_json(transaction_path, transaction);
    try {
      service("stop");
      verify_active(a, r.value("expected_active_build_hash", Json{}));
      atomic_write(target,
                   read_file(v / "build/new_libplc.so", 100 * 1024 * 1024));
      transaction["state"] = "swapped";
      save_json(transaction_path, transaction);
      save_json(active_path, next);
      transaction["state"] = "committed";
      save_json(transaction_path, transaction);
      resume();
    } catch (...) {
      auto failure = std::current_exception();
      service("stop");
      if (!a.value("build_hash", Json{}).is_null()) {
        require(file_hash(backup) == a.at("library_sha256").get<std::string>(),
                "Rollback backup differs; explicit recovery required");
        atomic_write(target, read_file(backup, 100 * 1024 * 1024));
      } else
        fs::remove(target);
      save_json(active_path, a);
      transaction["state"] = "rolled-back";
      save_json(transaction_path, transaction);
      resume(a.value("build_hash", Json{}).is_null());
      std::rethrow_exception(failure);
    }
    return {{"state", "activated-not-running"}, {"active", next}};
  }
  Json recover(const Json &r) {
    auto t = read_json(transaction_path);
    auto state = t.at("state");
    if (state == "committed" || state == "rolled-back") {
      auto a = active();
      require(a == t.at(state == "committed" ? "new_active" : "old_active"),
              "Terminal journal differs from active identity");
      verify_active(a, a.value("build_hash", Json{}));
      resume(a.value("build_hash", Json{}).is_null());
      return {{"state", state}, {"active", a}};
    }
    auto old = t.at("old_active");
    auto target = fs::path(old.at("library_path").get<std::string>());
    auto backup = fs::path(t.at("backup_path").get<std::string>());
    require(target.parent_path() == runtime / "build" &&
                backup.parent_path() == prefix / ".history",
            "Recovery paths outside private transaction");
    service("stop");
    if (old.value("build_hash", Json{}).is_null())
      fs::remove(target);
    else {
      require(file_hash(backup) == old.at("library_sha256").get<std::string>(),
              "Recovery original backup checksum missing/mismatched");
      atomic_write(target, read_file(backup, 100 * 1024 * 1024));
    }
    save_json(active_path, old);
    t["state"] = "rolled-back";
    save_json(transaction_path, t);
    resume(old.value("build_hash", Json{}).is_null());
    return {{"state", "rolled-back"}, {"active", old}};
  }
  Json export_program(bool sources) {
    auto a = active();
    verify_active(a, a.value("build_hash", Json{}));
    require(!a.value("build_hash", Json{}).is_null(),
            "Active version has no saved source archive");
    auto v = version(a.at("build_hash"));
    auto b = staged(v);
    auto compiled = compiled_candidate(v);
    require(compiled.at("library_sha256").get<std::string>() ==
                a.at("library_sha256").get<std::string>(),
            "Active program compiled identity differs");
    for(auto [key,manifest_key]:std::vector<std::pair<std::string,std::string>>{{"source_hash","SourceHash"},{"hardware_hash","HardwareHash"},{"symbol_table_hash","SymbolTableHash"}})if(a.contains(key)&&!a[key].is_null())require(identity(a[key])==identity(b.manifest.at(manifest_key)),"Active program metadata identity differs: "+key);
    auto debug = b.files.at("debug-map.json");
    require(sha256(debug) == lower(identity(b.manifest.at("SymbolTableHash"))),
            "Debug map checksum differs");
    Json transfer = {
        {"source_hash", b.manifest.value("SourceHash", Json{})},
        {"hardware_hash", b.manifest.value("HardwareHash", Json{})},
        {"symbol_table_hash", b.manifest.at("SymbolTableHash")},
        {"debug_map", parse_json(debug)},
        {"debug_map_json", debug}};
    if (sources) {
      require(b.files.contains("engineering-archive.json"),
              "该版本未保存源工程，无法还原 ST (source archive unavailable)");
      auto archive = parse_json(b.files.at("engineering-archive.json"));
      require(archive.value("SchemaVersion", Json{}) == 1 &&
                  archive.at("SourceHash") == b.manifest.at("SourceHash") &&
                  archive.at("HardwareHash") == b.manifest.at("HardwareHash") &&
                  archive.at("Project").is_object() &&
                  archive.at("Sources").is_array(),
              "Source archive identity differs");
      transfer["project"] = archive.at("Project");
      transfer["sources"] = archive.at("Sources");
    }
    require(transfer.dump().size() <= 20 * 1024 * 1024 && active() == a,
            "Export capacity exceeded or active changed");
    return {{"state", sources ? "program-exported" : "program-debug-map"},
            {"active", a},
            {"transfer", transfer}};
  }
  Json export_hardware() {
    auto a = active();
    auto record = fs::path(config.value(
        "ethercat_active_record", (prefix / "hardware-active.json").string()));
    auto applied = read_json(record);
    auto v = version(applied.at("buildHash"));
    auto b = staged(v);
    auto verified = verify_applied(v);
    matching(applied, verified);
    auto d = parse_json(b.files.at("hardware-configuration.json"));
    require(d.value("SchemaVersion", Json{}) == 1 &&
                d.value("State", Json{}) == "offline-engineering",
            "Unsupported saved hardware configuration");
    if (b.files.contains("axis-manifest.json")) {
      auto axes = parse_json(b.files.at("axis-manifest.json"));
      if (!axes.at("Axes").empty()) {
        auto cap = capability(channel(config, axes.at("InterfaceVersion")));
        require(cap.at("ManifestSha256") ==
                    sha256(b.files.at("axis-manifest.json")),
                "Loaded axis manifest differs from applied hardware");
      }
    }
    require(read_json(record) == applied && active() == a,
            "Hardware/program changed during export");
    return {{"state", "hardware-exported"},
            {"active", a},
            {"transfer", {{"configuration", d}, {"applied", verified}}}};
  }
  Json execute(const Json &r) {
    require(r.is_object() && r.value("request_id", Json{}).is_string() &&
                std::regex_match(r["request_id"].get<std::string>(),
                                 std::regex("[A-Za-z0-9_-]{1,80}")),
            "Safe request ID required");
    auto action = r.value("action", std::string{});
    if (action == "realtime-status")
      return {{"success", true},
              {"state", "diagnostic"},
              {"request_id", r.at("request_id")},
              {"realtime", realtime_status()}};
    Fd owner(::open((prefix / "owner.lock").c_str(),
                    O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW, 0600));
    require(bool(owner) && flock(owner.get(), LOCK_EX | LOCK_NB) == 0,
            "Another deployment owns the instance");
    if (fs::exists(transaction_path)) {
      auto t = read_json(transaction_path);
      require(t.value("state", Json{}) == "committed" ||
                  t.value("state", Json{}) == "rolled-back" ||
                  action == "status" || action == "recover",
              "Unfinished activation transaction requires explicit recovery");
    }
    Json result;
    if (action == "realtime-status")
      result = {{"state", "diagnostic"}, {"realtime", realtime_status()}};
    else if (action == "stage" || action == "hardware-stage")
      result = stage(r, action == "hardware-stage");
    else if (action == "compile") {
      try {
        result = compile(r);
      } catch (const std::exception &e) {
        result = {{"state", "compile-failed"},
                  {"build_hash", identity(r.at("build_hash"))},
                  {"message", e.what()},
                  {"request_id", r.at("request_id")}};
        auto v = version(r.at("build_hash"));
        if (fs::exists(v))
          save_json(v / "compile-result.json", result);
      }
    } else if (action == "configuration-plan")
      result = {{"state", "configuration-planned"},
                {"configuration_plan", plan(version(r.at("build_hash")))}};
    else if (action == "configuration-apply")
      result = apply_configuration(r);
    else if (action == "hardware-validate")
      result = {{"state", "hardware-validated"},
                {"hardware", hardware_bundle(version(r.at("build_hash")))}};
    else if (action == "hardware-apply")
      result = apply_hardware(r);
    else if (action == "activate")
      result = activate(r);
    else if (action == "recover")
      result = recover(r);
    else if (action == "program-export" || action == "program-debug-map")
      result = export_program(action == "program-export");
    else if (action == "hardware-export")
      result = export_hardware();
    else if (action == "status") {
      result = {{"state", "unavailable"},
                {"runtime_confirmed", false},
                {"active", fs::exists(active_path) ? active() : Json{}},
                {"transaction", fs::exists(transaction_path)
                                    ? read_json(transaction_path)
                                    : Json{}}};
      result["native_migration"] =
          fs::exists(prefix / "legacy-native-migration.json")
              ? read_json(prefix / "legacy-native-migration.json")
              : Json{};
      if (r.contains("build_hash") && !r["build_hash"].is_null()) {
        auto v = version(r["build_hash"]);
        result["candidate"] = read_json(
            v / (fs::exists(v / "compile-result.json") ? "compile-result.json"
                                                       : "stage-result.json"));
      }
      try {
        Runtime connection(config);
        result["state"] = lower(connection.status());
        result["runtime_confirmed"] = true;
      } catch (...) {
        result["message"] = "Runtime unavailable; disk identity returned, "
                            "device state unconfirmed";
      }
    } else if (action == "start" || action == "stop") {
      auto a = active();
      verify_active(a, r.value("expected_active_build_hash", Json{}));
      if (action == "start") {
        require(!a.value("build_hash", Json{}).is_null(),
                "Cannot start an empty baseline");
        verify_start_hardware(a);
      }
      Runtime connection(config);
      result = {{"state", lower(connection.transition(action))}, {"active", a}};
    } else if (action == "adopt-baseline" || action == "adopt-empty-baseline") {
      require(!fs::exists(active_path), "Baseline already adopted");
      auto libs = libraries();
      Runtime connection(config);
      auto state = connection.status();
      Json a;
      if (action == "adopt-empty-baseline") {
        require(
            (state == "EMPTY" ||
             (state == "STOPPED" &&
              config.value("safe_mode_verified", Json{}) == true)) &&
                libs.empty(),
            "Empty baseline requires stopped Runtime and no program library");
        a = {{"build_hash", nullptr},
             {"library_path",
              (runtime / "build/libplc_stworkbench.so").string()},
             {"library_sha256", nullptr}};
      } else {
        require(state == "STOPPED" && libs.size() == 1,
                "Baseline requires STOPPED and one library");
        auto hash = file_hash(libs[0]);
        a = {{"build_hash", "baseline-" + hash},
             {"library_path", libs[0].string()},
             {"library_sha256", hash}};
      }
      a["request_id"] = r.at("request_id");
      a["runtime_commit"] = RuntimeCommit;
      save_json(active_path, a);
      result = {{"state", action == "adopt-empty-baseline"
                              ? "baseline-empty"
                              : "baseline-adopted"},
                {"active", a}};
    } else
      throw std::runtime_error("Unsupported explicit deployment action");
    if (result.contains("active") && !result["active"].is_null())
      result["hardware_matches_active"] = hardware_matches(result["active"]);
    result["success"] = result.value("state", Json{}) != "compile-failed";
    result["request_id"] = r.at("request_id");
    return result;
  }
};
} // namespace stworkbench::deployment

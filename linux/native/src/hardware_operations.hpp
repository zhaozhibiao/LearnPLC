// SPDX-License-Identifier: MIT
#pragma once
#include "hardware_private.hpp"
namespace stworkbench::hardware {
struct TempDir {
  fs::path path;
  TempDir(const fs::path &parent, const char *prefix) {
    root_owned(parent);
    std::string p = (parent / (std::string(prefix) + "XXXXXX")).string();
    std::vector<char> b(p.begin(), p.end());
    b.push_back(0);
    require(::mkdtemp(b.data()), "Temporary directory failed");
    path = b.data();
  }
  ~TempDir() {
    std::error_code e;
    fs::remove_all(path, e);
  }
};
inline void mkdir_root(const fs::path &p) {
  root_owned(p.parent_path());
  if (::mkdir(p.c_str(), 0750) != 0)
    require(errno == EEXIST, "Root mkdir failed");
  root_owned(p);
  require(fs::is_directory(p), "Root directory required");
}
inline gid_t approved_group() {
  auto g = ::getgrnam("a");
  require(g, "Approved runtime group missing");
  return g->gr_gid;
}
inline void share_root(const fs::path &p) {
  root_owned(p);
  require(::chown(p.c_str(), 0, approved_group()) == 0,
          "Root group assignment failed");
  Fd fd(::open(p.c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW));
  require(fd && ::fsync(fd.get()) == 0, "Root metadata fsync failed");
}
inline Json json_command(const std::vector<std::string> &a) {
  return object(command(a).output);
}
inline std::string plc_status() {
  auto user = ::getpwnam("a");
  require(user, "Approved user missing");
  fs::path credentials = "/home/a/.local/share/st-workbench/runtime-v4.3.0/"
                         "data/coordinator-auth.json";
  struct stat s{};
  require(::lstat(credentials.c_str(), &s) == 0 && s.st_uid == user->pw_uid &&
              (s.st_mode & 0777) == 0600,
          "Credentials must be private approved-user file");
  auto body = capture(credentials, 16384, user->pw_uid, 0600);
  object(body);
  auto ca = capture("/home/a/.local/share/st-workbench/runtime-v4.3.0/source/"
                    "webserver/certOPENPLC.pem");
  TempDir temp("/run", ".hardware-auth-");
  atomic_write(temp.path / "body", body);
  atomic_write(temp.path / "ca", ca);
  auto call = [&](std::string endpoint, bool login) {
    std::vector<std::string> args = {"/usr/bin/curl",
                                     "-q",
                                     "--silent",
                                     "--show-error",
                                     "--fail",
                                     "--proto",
                                     "=https",
                                     "--max-time",
                                     "3",
                                     "--max-redirs",
                                     "0",
                                     "--noproxy",
                                     "*",
                                     "--cacert",
                                     (temp.path / "ca").string(),
                                     "--header",
                                     "Content-Type: application/json"};
    if (login) {
      args.insert(args.end(),
                  {"--data-binary", "@" + (temp.path / "body").string()});
    } else
      args.insert(args.end(),
                  {"--header", "@" + (temp.path / "token").string()});
    args.push_back("https://localhost:8443" + endpoint);
    return json_command(args);
  };
  auto token = call("/api/login", true).value("access_token", Json());
  require(token.is_string(), "Invalid Runtime token");
  auto t = token.get<std::string>();
  require(!t.empty() && t.size() <= 8192 &&
              std::all_of(t.begin(), t.end(),
                          [](unsigned char c) { return c >= 33 && c <= 126; }),
          "Invalid Runtime token");
  atomic_write(temp.path / "token", "Authorization: Bearer " + t + "\n");
  auto status = call("/api/status", false).value("status", Json());
  require(status == "STATUS:STOPPED" || status == "STATUS:RUNNING" ||
              status == "STATUS:EMPTY" || status == "STATUS:TRANSITIONING",
          "Invalid PLC status");
  return status.get<std::string>().substr(7);
}
inline Json service_state() {
  auto r =
      command({"/usr/bin/systemctl", "show", service, "--property=LoadState",
               "--property=ActiveState", "--property=MainPID"},
              false);
  Json j = Json::object();
  std::istringstream in(r.output);
  std::string line;
  while (std::getline(in, line)) {
    auto p = line.find('=');
    require(p != line.npos, "Malformed service state");
    auto k = line.substr(0, p);
    require((k == "LoadState" || k == "ActiveState" || k == "MainPID") &&
                !j.contains(k),
            "Unknown service state field");
    j[k] = line.substr(p + 1);
  }
  require(j.size() == 3, "Incomplete service state");
  j["exitCode"] = r.exit_code;
  return j;
}
inline void release() {
  auto end = monotonic_ns() + 3000000000LL;
  for (;;) {
    auto s = service_state();
    require(s["MainPID"] == "0" && (s["ActiveState"] == "inactive" ||
                                    s["ActiveState"] == "failed"),
            "Core is not stopped");
    if (s["LoadState"] == "not-found" &&
        !fs::exists(fs::path("/run/systemd/transient") / service))
      return;
    require(monotonic_ns() < end, "Old transient core retained");
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
  }
}
inline bool preflight(bool initial) {
  require(plc_status() == "STOPPED", "PLC must be stopped");
  struct stat info{};
  if (initial && ::lstat(controller, &info) != 0 && errno == ENOENT) {
    auto s = service_state();
    require(
        s["MainPID"] == "0" &&
            (s["ActiveState"] == "inactive" || s["ActiveState"] == "failed") &&
            (s["LoadState"] == "not-found" ||
             (s["LoadState"] == "loaded" && s["exitCode"] == 0)),
        "Initial core state unconfirmed");
    return true;
  }
  native("HelloRequest",
         {{"clientName", "hardware-preflight"}, {"clientVersion", "1"}},
         "HelloResponse");
  disabled(native("ReadSnapshotRequest", {{"includeDiagnostics", false}},
                  "SnapshotResponse"),
           false);
  return false;
}
inline std::vector<std::string> trace(const Json &machine,
                                      const std::string &fingerprint) {
  auto t = machine.value("axisTrace", Json());
  if (t.is_null())
    return {};
  require(t.is_object() && t.size() <= 2 && t.contains("enabled") &&
              t["enabled"].is_boolean(),
          "Invalid trace settings");
  for (auto it = t.begin(); it != t.end(); ++it)
    require(it.key() == "enabled" || it.key() == "capacity",
            "Unknown trace setting");
  auto cap = t.value("capacity", Json(0));
  require(integer(cap) && cap >= 0 && cap <= 120000, "Invalid trace capacity");
  if (t["enabled"] == false)
    return {};
  require(cap > 0 && machine.value("axes", Json()).is_array() &&
              machine["axes"].size() == 1,
          "Trace requires one axis and capacity");
  auto r =
      capabilities(machine.value("requiredRuntimeCapabilities", Json::array()));
  require(r.contains("workbench_motion_migration_v1") &&
              r.contains("workbench_axis_trace_v1"),
          "Trace capabilities missing");
  mkdir_root("/var/lib/st-workbench-ethercat/traces");
  share_root("/var/lib/st-workbench-ethercat/traces");
  auto nonce =
      sha256(std::to_string(monotonic_ns()) + std::to_string(::getpid()))
          .substr(0, 32);
  auto path = fs::path("/var/lib/st-workbench-ethercat/traces") /
              (fingerprint + "-" + nonce + ".csv");
  require(!fs::exists(path), "Trace already exists");
  return {"--axis-trace-csv", path.string(), "--axis-trace-capacity",
          cap.dump()};
}
inline std::vector<std::string> arguments(const fs::path &directory) {
  return {"--ethercat-config", (directory / files[0]).string(),
          "--machine-config",  (directory / files[1]).string(),
          "--io-map",          (directory / files[2]).string()};
}
inline void validate_core(const Installation &i, const fs::path &dir,
                          const std::vector<std::string> &t) {
  auto args = arguments(dir);
  args[0] = "--validate-ethercat-config";
  args.insert(args.begin(), i.binary);
  args.insert(args.end(), t.begin(), t.end());
  command(args);
}
inline void start(const Installation &i, const fs::path &dir,
                  const std::vector<std::string> &t) {
  std::vector<std::string> a = {
      "/usr/bin/systemd-run",
      "--unit=st-workbench-is620n",
      "--service-type=exec",
      "--uid=root",
      "--gid=a",
      "--property=LimitMEMLOCK=infinity",
      "--property=LimitRTPRIO=95",
      "--property=NoNewPrivileges=yes",
      "--property=UMask=0027",
      "--property=TimeoutStopSec=5",
      "--property=KillMode=control-group",
      "--property=RuntimeMaxSec=infinity",
      "--property=CPUAffinity=2",
      "--property=After=st-workbench-cpu-isolation.service",
      "--property=RuntimeDirectory=st-workbench-is620n",
      "--property=RuntimeDirectoryMode=0750",
      "--",
      i.binary};
  auto paths = arguments(dir);
  a.insert(a.end(), paths.begin(), paths.end());
  a.insert(a.end(), t.begin(), t.end());
  a.insert(a.end(), {"--socket", controller, "--cpu", "2"});
  command(a);
}
inline void windows(const Json &topology) {
  for (auto &slave : topology.at("slaves"))
    for (auto &s : slave.value("startupSdos", Json::array()))
      if (s.value("index", Json()) == 0x6065 &&
          s.value("subindex", Json()) == 0) {
        auto d = s.value("data", Json());
        require(d.is_array() && d.size() == 4,
                "Invalid drive following window");
        std::uint32_t value = 0;
        for (int n = 0; n < 4; ++n) {
          require(integer(d[n]) && d[n] >= 0 && d[n] <= 255,
                  "Invalid drive window byte");
          value |= d[n].get<unsigned>() << (n * 8);
        }
        auto p = slave.value("position", Json());
        require(integer(p) && p >= 0 && p < 65536, "Invalid drive position");
        auto out = command({"/opt/etherlab/bin/ethercat", "upload", "-p",
                            p.dump(), "-t", "uint32", "0x6065", "0"})
                       .output;
        std::istringstream in(out);
        std::string part, last;
        while (in >> part)
          last = part;
        require(!last.empty(), "Missing drive window readback");
        std::size_t end = 0;
        auto actual = std::stoull(last, &end, 10);
        require(end == last.size() && actual == value,
                "Drive window readback differs");
      }
}
inline void ready(const Bundle &b) {
  auto end = monotonic_ns() + 10000000000LL;
  for (;;) {
    try {
      auto h =
          native("HelloRequest",
                 {{"clientName", "hardware-apply"}, {"clientVersion", "1"}},
                 "HelloResponse");
      loaded(h, b.required);
      require(h.value("runtimeReady", Json()) == true &&
                  h.value("ethercatTopologyFingerprint", Json()) ==
                      b.proof["topologyFingerprint"],
              "Loaded core identity mismatch");
      auto a = native("ReadSnapshotRequest", {{"includeDiagnostics", false}},
                      "SnapshotResponse");
      disabled(a, true);
      std::this_thread::sleep_for(std::chrono::milliseconds(20));
      auto c = native("ReadSnapshotRequest", {{"includeDiagnostics", false}},
                      "SnapshotResponse");
      disabled(c, true);
      for (auto k : {"sampleCycle", "sampleMonotonicNs"})
        require(integer(a.value(k, Json())) && integer(c.value(k, Json())) &&
                    a[k] > 0 && c[k] > a[k],
                "Core samples not advancing");
      windows(b.topology);
      return;
    } catch (...) {
      if (monotonic_ns() >= end)
        throw;
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
  }
}
struct ApplyLock {
  Fd fd;
  ApplyLock() {
    root_owned("/run");
    fd.reset(::open("/run/stworkbench-ethercat-apply.lock",
                    O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK,
                    0600));
    struct stat s{};
    require(fd && ::fstat(fd.get(), &s) == 0 && S_ISREG(s.st_mode) &&
                s.st_uid == 0 && (s.st_mode & 0777) == 0600,
            "Invalid apply lock");
    require(::flock(fd.get(), LOCK_EX | LOCK_NB) == 0,
            "Hardware apply is already running");
  }
};
inline Json apply(const std::string &build) {
  require(::geteuid() == 0, "Root required");
  ApplyLock lock;
  auto i = installation();
  approved_group();
  auto b = candidate(identity(build));
  check_binary(i, b.required);
  mkdir_root("/var/lib/st-workbench-ethercat");
  mkdir_root(versions);
  auto dest =
      fs::path(versions) / b.proof["topologyFingerprint"].get<std::string>();
  {
    TempDir stage(versions, ".apply-");
    for (int n = 0; n < 4; ++n) {
      atomic_write(stage.path / files[n], b.bytes[n], 0640);
      share_root(stage.path / files[n]);
    }
    share_root(stage.path);
    std::array<std::string, 4> bytes;
    for (int n = 0; n < 4; ++n)
      bytes[n] = capture(stage.path / files[n]);
    require(verify(bytes, build).proof == b.proof, "Stage integrity mismatch");
    if (fs::exists(dest)) {
      root_owned(dest);
      std::array<std::string, 4> existing;
      for (int n = 0; n < 4; ++n) {
        root_owned(dest / files[n]);
        existing[n] = capture(dest / files[n]);
        if (n < 3)
          require(existing[n] == b.bytes[n], "Existing root topology differs");
      }
      auto old_manifest = object(existing[3]);
      auto old =
          verify(existing, identity(old_manifest.value("BuildHash", Json())));
      require(old.proof["topologyFingerprint"] ==
                  b.proof["topologyFingerprint"],
              "Existing root cache identity differs");
    } else {
      require(::chmod(stage.path.c_str(), 0750) == 0, "Stage mode failed");
      require(::rename(stage.path.c_str(), dest.c_str()) == 0,
              "Root cache publish failed");
      Fd d(::open(versions, O_DIRECTORY | O_RDONLY | O_CLOEXEC));
      require(d && ::fsync(d.get()) == 0, "Root cache fsync failed");
    }
  }
  auto t = trace(b.machine, b.proof["topologyFingerprint"]);
  validate_core(i, dest, t);
  bool initial = preflight(i.config.value("allowInitialStart", Json()) == true);
  bool touched = false;
  try {
    touched = true;
    if (!initial) {
      command({"/usr/bin/systemctl", "stop", service});
      command({"/usr/bin/systemctl", "reset-failed", service}, false);
    }
    release();
    start(i, dest, t);
    ready(b);
    auto record = b.proof;
    record.erase("status");
    root_owned(fs::path(active_path).parent_path());
    atomic_write(active_path, record.dump(), 0640);
    share_root(active_path);
    auto result = b.proof;
    result.update({{"status", "applied"},
                   {"verified", true},
                   {"rollback", "not-required"}});
    return result;
  } catch (const std::exception &e) {
    auto r = b.proof;
    r.update({{"status", "failed"},
              {"nativeState", "unknown"},
              {"rollback", "not-attempted-unknown-baseline"},
              {"error", std::string(e.what()).substr(0, 512)}});
    if (touched)
      try {
        if (command({"/usr/bin/systemctl", "stop", service}, false).exit_code ==
            0)
          r["nativeState"] = "stopped";
      } catch (...) {
      }
    return r;
  }
}
inline Bundle saved() {
  root_owned(active_path);
  auto a = object(capture(active_path));
  auto fingerprint = identity(a.value("topologyFingerprint", Json()));
  auto dir = fs::path(versions) / fingerprint;
  root_owned(dir);
  std::array<std::string, 4> bytes;
  for (int n = 0; n < 4; ++n) {
    root_owned(dir / files[n]);
    bytes[n] = capture(dir / files[n]);
  }
  auto manifest = object(bytes[3]);
  auto b = verify(bytes, identity(manifest.value("BuildHash", Json())));
  require(b.proof["topologyFingerprint"] == fingerprint &&
              b.proof["machineSha256"] ==
                  identity(a.value("machineSha256", Json())) &&
              b.proof["ioMapSha256"] ==
                  identity(a.value("ioMapSha256", Json())),
          "Saved active identity mismatch");
  identity(a.value("buildHash", Json()));
  b.required =
      requirements(b.machine, a.value("runtimeRequirements", manifest));
  return b;
}
inline std::string restore() {
  require(::geteuid() == 0, "Root required");
  ApplyLock lock;
  auto i = installation();
  auto b = saved();
  check_binary(i, b.required);
  auto s = service_state();
  auto pid = s["MainPID"].get<std::string>();
  require(!pid.empty() && pid.find_first_not_of("0123456789") == pid.npos,
          "Invalid service PID");
  if (s["LoadState"] == "loaded" && s["ActiveState"] == "active" &&
      pid != "0" && s["exitCode"] == 0) {
    auto h = native("HelloRequest",
                    {{"clientName", "boot-restore"}, {"clientVersion", "1"}},
                    "HelloResponse");
    loaded(h, b.required);
    require(h.value("ethercatTopologyFingerprint", Json()) ==
                b.proof["topologyFingerprint"],
            "Running core topology mismatch");
    return "already-running";
  }
  require(
      (s["LoadState"] == "loaded" || s["LoadState"] == "not-found") &&
          (s["ActiveState"] == "inactive" || s["ActiveState"] == "failed") &&
          pid == "0" && (s["LoadState"] != "loaded" || s["exitCode"] == 0),
      "Core state unconfirmed");
  auto dir =
      fs::path(versions) / b.proof["topologyFingerprint"].get<std::string>();
  auto t = trace(b.machine, b.proof["topologyFingerprint"]);
  validate_core(i, dir, t);
  require(plc_status() == "STOPPED", "PLC must be stopped for restore");
  command({"/usr/bin/systemctl", "reset-failed", service}, false);
  release();
  try {
    start(i, dir, t);
    ready(b);
  } catch (...) {
    try {
      command({"/usr/bin/systemctl", "stop", service}, false);
    } catch (...) {
    }
    throw;
  }
  return "restored-disabled";
}
} // namespace stworkbench::hardware

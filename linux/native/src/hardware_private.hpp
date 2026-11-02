// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <array>
#include <cctype>
#include <cerrno>
#include <cstring>
#include <fcntl.h>
#include <grp.h>
#include <iostream>
#include <poll.h>
#include <pwd.h>
#include <set>
#include <sstream>
#include <stworkbench/common.hpp>
#include <sys/file.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <thread>
namespace stworkbench::hardware {
namespace fs = std::filesystem;
inline constexpr const char *installation_path =
    "/etc/st-workbench-ethercat/installation.json";
inline constexpr const char *candidates =
    "/home/a/.local/share/st-workbench/runtime-v4.3.0/deployment/.versions";
inline constexpr const char *versions =
    "/var/lib/st-workbench-ethercat/versions";
inline constexpr const char *active_path =
    "/var/lib/st-workbench-ethercat/active.json";
inline constexpr const char *service = "st-workbench-is620n.service";
inline constexpr const char *controller =
    "/run/st-workbench-is620n/controller.sock";
inline constexpr const char *ledger_path =
    "/var/lib/st-workbench-ethercat/broker-requests.json";
inline constexpr const char *broker_socket =
    "/run/st-workbench-hardware/control.sock";
inline const std::array<std::string, 4> files = {
    "ethercat-runtime.json", "machine-config.json", "io-map.json",
    "build-manifest.json"};
inline void require(bool value, const char *message) {
  if (!value)
    throw std::runtime_error(message);
}
inline bool integer(const Json &j) {
  return j.is_number_integer() || j.is_number_unsigned();
}
inline std::string identity(Json j) {
  require(j.is_string(), "Invalid SHA256 identity");
  auto s = j.get<std::string>();
  require(s.size() == 64 &&
              s.find_first_not_of("0123456789abcdefABCDEF") == s.npos,
          "Invalid SHA256 identity");
  std::transform(s.begin(), s.end(), s.begin(),
                 [](unsigned char c) { return std::tolower(c); });
  return s;
}
inline void root_owned(fs::path path, int mode = -1) {
  require(path.is_absolute(), "Root path must be absolute");
  for (auto p = path;; p = p.parent_path()) {
    struct stat s{};
    require(::lstat(p.c_str(), &s) == 0 && !S_ISLNK(s.st_mode) &&
                s.st_uid == 0 && !(s.st_mode & 0022),
            "Path is not exclusively root controlled");
    if (p == path && mode >= 0)
      require((s.st_mode & 0777) == mode, "Root file mode mismatch");
    if (p == p.root_path())
      break;
  }
}
// Component-by-component openat prevents parent symlink replacement during
// capture.
inline std::string capture(const fs::path &path,
                           std::size_t maximum = 4 * 1024 * 1024,
                           int owner = -1, int mode = -1) {
  require(path.is_absolute(), "Absolute capture path required");
  Fd dir(::open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC));
  std::vector<std::string> parts;
  for (auto &p : path.relative_path()) {
    require(p != ".." && p != ".", "Invalid path component");
    parts.push_back(p.string());
  }
  require(!parts.empty(), "Missing file");
  for (std::size_t i = 0; i + 1 < parts.size(); ++i) {
    Fd next(::openat(dir.get(), parts[i].c_str(),
                     O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC));
    require(bool(next), "Unsafe capture directory");
    dir = std::move(next);
  }
  Fd f(::openat(dir.get(), parts.back().c_str(),
                O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC));
  struct stat s{};
  require(f && ::fstat(f.get(), &s) == 0 && S_ISREG(s.st_mode) &&
              s.st_size >= 0 && std::uint64_t(s.st_size) <= maximum,
          "Invalid bounded regular file");
  require(owner < 0 || s.st_uid == static_cast<uid_t>(owner),
          "Captured owner mismatch");
  require(mode < 0 || (s.st_mode & 0777) == mode, "Captured mode mismatch");
  std::string out;
  char b[65536];
  for (;;) {
    auto n = ::read(f.get(), b, sizeof b);
    if (n < 0 && errno == EINTR)
      continue;
    require(n >= 0, "Capture read failed");
    if (!n)
      break;
    require(out.size() + n <= maximum, "Capture capacity exceeded");
    out.append(b, n);
  }
  return out;
}
inline Json object(std::string_view s) {
  auto j = parse_json(s);
  require(j.is_object(), "Expected JSON object");
  return j;
}
inline std::set<std::string> capabilities(const Json &j) {
  static const std::set<std::string> known = {
      "workbench_motion_migration_v1", "workbench_axis_trace_v1",
      "workbench_axis_move_profile_v2", "workbench_axis_velocity_profile_v2"};
  require(j.is_array() && j.size() <= 16,
          "Invalid required runtime capabilities");
  std::set<std::string> out;
  for (auto &x : j) {
    require(x.is_string() && known.contains(x.get<std::string>()),
            "Unsupported required runtime capability");
    out.insert(x.get<std::string>());
  }
  return out;
}
inline std::set<std::string> requirements(const Json &machine,
                                          const Json &manifest) {
  require(machine.is_object() && manifest.is_object(),
          "Invalid capability manifest");
  auto r =
      capabilities(machine.value("requiredRuntimeCapabilities", Json::array()));
  auto p = capabilities(
      manifest.value("RequiredRuntimeCapabilities", Json::array()));
  auto v = manifest.value("AxisInterfaceVersion", Json(nullptr));
  require(v.is_null() || v == "workbench-axis-v1" || v == "workbench-axis-v2",
          "Unsupported axis ABI");
  if (v == "workbench-axis-v2")
    require(manifest.value("AxisExportMarker", Json()) == "workbench_axis_v2" &&
                integer(manifest.value("AxisCommandBytes", Json())) &&
                manifest["AxisCommandBytes"] == 4112 &&
                integer(manifest.value("AxisFeedbackBytes", Json())) &&
                manifest["AxisFeedbackBytes"] == 6656 &&
                p.contains("workbench_axis_move_profile_v2"),
            "Incomplete axis v2 ABI proof");
  else
    require(!p.contains("workbench_axis_move_profile_v2") &&
                !p.contains("workbench_axis_velocity_profile_v2"),
            "Dynamic capabilities require axis v2");
  r.insert(p.begin(), p.end());
  return r;
}
inline void loaded(const Json &hello, const std::set<std::string> &required) {
  if (required.empty())
    return;
  auto a = hello.value("runtimeCapabilities", Json());
  require(a.is_array() && a.size() <= 64, "Missing runtime capabilities");
  std::set<std::string> actual;
  for (auto &x : a) {
    require(x.is_string(), "Invalid runtime capability");
    actual.insert(x.get<std::string>());
  }
  for (auto &x : required)
    require(actual.contains(x), "Runtime capability is missing");
}
inline Json request(Json r) {
  require(r.is_object(), "Expected request object");
  auto a = r.value("action", Json());
  require(a == "apply" || a == "validate" || a == "status",
          "Unsupported action");
  require(r.size() == (a == "status" ? 3 : 4) &&
              r.contains("protocolVersion") && r.contains("requestId"),
          "Unexpected broker fields");
  require(integer(r["protocolVersion"]) && r["protocolVersion"] == 1,
          "Invalid broker protocol");
  require(r["requestId"].is_string(), "Invalid request ID");
  auto id = r["requestId"].get<std::string>();
  require(!id.empty() && id.size() <= 80 &&
              id.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ"
                                   "RSTUVWXYZ0123456789_-") == id.npos,
          "Invalid request ID");
  if (a != "status") {
    require(r.contains("buildHash"), "Missing build hash");
    identity(r["buildHash"]);
  }
  return r;
}
inline void disabled(const Json &s, bool zero) {
  require(s.value("runtimeReady", Json()) == true &&
              s.value("ioReady", Json()) == true &&
              s.value("outputsAtConfiguredSafeState", Json()) == true,
          "Native safe-state proof missing");
  auto a = s.value("axes", Json());
  require(a.is_array() && a.size() <= 64, "Invalid native axes");
  for (auto &x : a) {
    require(x.is_object(), "Invalid axis");
    auto raw = x.value("driveStatusword", Json());
    int word = -1;
    if (!raw.is_null()) {
      require(integer(raw) && raw >= 0 && raw <= 65535,
              "Invalid drive statusword");
      word = raw.get<int>() & 0x6f;
      require(word == 8 || word == 64 || word == 33 || word == 35,
              "Drive torque not disabled");
    }
    bool recovered = x.value("state", Json()) == "ErrorStop" &&
                     x.value("motionBusy", Json()) == false && word == 8;
    require(recovered || (x.value("state", Json()) == "Disabled" &&
                          x.contains("fault") && x["fault"].is_null()),
            "Axis is not confirmed disabled");
  }
  if (zero)
    require(integer(s.value("lastAppliedCommandSequence", Json())) &&
                s["lastAppliedCommandSequence"] == 0,
            "Core already accepted commands");
}
inline ProcessResult command(const std::vector<std::string> &args,
                             bool checked = true) {
  require(!args.empty() && fs::path(args[0]).is_absolute(),
          "Absolute executable required");
  root_owned(args[0]);
  auto r = run_process(args, "/", std::chrono::seconds(20), 1024 * 1024, true);
  require(!r.timed_out && !r.truncated, "Command exceeded bounds");
  if (checked)
    require(r.exit_code == 0, "System command failed");
  return r;
}
inline void transfer(int fd, char *data, std::size_t size, bool write,
                     std::int64_t deadline) {
  while (size) {
    auto left = (deadline - monotonic_ns()) / 1000000;
    require(left > 0, "Frame deadline exceeded");
    pollfd p{fd, short(write ? POLLOUT : POLLIN), 0};
    int n = ::poll(&p, 1, int(std::min<std::int64_t>(left, 1000)));
    if (n < 0 && errno == EINTR)
      continue;
    if (n == 0) {
      if (monotonic_ns() >= deadline)
        throw std::runtime_error("Frame deadline exceeded");
      continue;
    }
    require(n > 0, "Frame deadline exceeded");
    auto count = write ? ::send(fd, data, size, MSG_NOSIGNAL | MSG_DONTWAIT)
                       : ::recv(fd, data, size, MSG_DONTWAIT);
    if (count < 0 && (errno == EINTR || errno == EAGAIN))
      continue;
    require(count > 0, "Truncated frame");
    data += count;
    size -= count;
  }
}
inline std::string frame_read(int fd, std::size_t max) {
  auto deadline = monotonic_ns() + 3000000000LL;
  unsigned char b[4];
  transfer(fd, reinterpret_cast<char *>(b), 4, false, deadline);
  std::size_t n = std::uint32_t(b[0]) | (std::uint32_t(b[1]) << 8) |
                  (std::uint32_t(b[2]) << 16) | (std::uint32_t(b[3]) << 24);
  require(n > 0 && n <= max, "Frame capacity exceeded");
  std::string s(n, '\0');
  transfer(fd, s.data(), n, false, deadline);
  return s;
}
inline void frame_write(int fd, const Json &j) {
  auto s = j.dump();
  require(s.size() <= 1024 * 1024, "Response exceeds capacity");
  std::uint32_t n = s.size();
  char b[4] = {char(n), char(n >> 8), char(n >> 16), char(n >> 24)};
  auto deadline = monotonic_ns() + 3000000000LL;
  transfer(fd, b, 4, true, deadline);
  transfer(fd, s.data(), s.size(), true, deadline);
}
inline Json native(const std::string &kind, const Json &payload,
                   const std::string &expected) {
  Fd s(::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0));
  require(bool(s), "Socket creation failed");
  sockaddr_un a{};
  a.sun_family = AF_UNIX;
  std::strcpy(a.sun_path, controller);
  if (::connect(s.get(), reinterpret_cast<sockaddr *>(&a), sizeof a) != 0) {
    require(errno == EINPROGRESS || errno == EAGAIN, "Native unavailable");
    pollfd p{s.get(), POLLOUT, 0};
    require(::poll(&p, 1, 3000) > 0, "Native connect deadline");
    int error = 0;
    socklen_t size = sizeof error;
    require(::getsockopt(s.get(), SOL_SOCKET, SO_ERROR, &error, &size) == 0 &&
                error == 0,
            "Native connect failed");
  }
  auto id = std::to_string(::getpid()) + "-" + std::to_string(monotonic_ns());
  frame_write(s.get(), {{"protocolVersion", 4},
                        {"kind", kind},
                        {"requestId", id},
                        {"sequence", 1},
                        {"payload", payload}});
  auto r = object(frame_read(s.get(), 1024 * 1024));
  require(r.value("protocolVersion", Json()) == 4 &&
              r.value("requestId", Json()) == id &&
              r.value("sequence", Json()) == 1 &&
              r.value("kind", Json()) == expected &&
              r.value("payload", Json()).is_object(),
          "Native response identity mismatch");
  return r["payload"];
}
struct Bundle {
  std::array<std::string, 4> bytes;
  Json manifest, machine, topology, proof;
  std::set<std::string> required;
};
inline Bundle verify(std::array<std::string, 4> bytes,
                     const std::string &build) {
  Bundle b;
  b.bytes = std::move(bytes);
  b.manifest = object(b.bytes[3]);
  require(identity(b.manifest.value("BuildHash", Json())) == identity(build),
          "Build identity mismatch");
  auto inv = b.manifest.value("Files", Json());
  require(inv.is_object(), "Missing file inventory");
  for (int i = 0; i < 3; ++i)
    require(identity(inv.value(files[i], Json())) == sha256(b.bytes[i]),
            "Hardware inventory mismatch");
  b.machine = object(b.bytes[1]);
  object(b.bytes[2]);
  b.topology = object(b.bytes[0]);
  auto t = b.topology;
  auto fingerprint = identity(t.value("topologyFingerprint", Json()));
  t.erase("topologyFingerprint");
  require(integer(t.value("schemaVersion", Json())) &&
              t["schemaVersion"] == 1 &&
              t.value("readyForRuntime", Json()) == true &&
              t.value("blockers", Json()) == Json::array(),
          "Topology blockers unresolved");
  require(sha256(t.dump(-1, ' ', false, Json::error_handler_t::strict)) ==
              fingerprint,
          "Topology canonical identity mismatch");
  require(identity(t.value("machineSha256", Json())) == sha256(b.bytes[1]) &&
              identity(t.value("ioMapSha256", Json())) == sha256(b.bytes[2]),
          "Topology companion mismatch");
  require(t.value("slaves", Json()).is_array() && !t["slaves"].empty() &&
              t["slaves"].size() <= 64,
          "Invalid slave topology");
  b.required = requirements(b.machine, b.manifest);
  b.proof = {{"status", "validated"},
             {"buildHash", identity(build)},
             {"topologyFingerprint", fingerprint},
             {"machineSha256", sha256(b.bytes[1])},
             {"ioMapSha256", sha256(b.bytes[2])}};
  if (b.manifest.value("AxisInterfaceVersion", Json()) == "workbench-axis-v2" ||
      !b.manifest.value("RequiredRuntimeCapabilities", Json::array()).empty()) {
    Json r = Json::object();
    for (auto k : {"AxisInterfaceVersion", "AxisExportMarker",
                   "AxisCommandBytes", "AxisFeedbackBytes"})
      if (b.manifest.contains(k))
        r[k] = b.manifest[k];
    r["RequiredRuntimeCapabilities"] = b.required;
    b.proof["runtimeRequirements"] = r;
  }
  return b;
}
inline Bundle candidate(const std::string &build) {
  identity(build);
  std::array<std::string, 4> bytes;
  for (int i = 0; i < 4; ++i)
    bytes[i] =
        capture(fs::path(candidates) / build / "core/generated" / files[i]);
  return verify(std::move(bytes), build);
}
struct Installation {
  Json config;
  std::string binary;
};
inline Installation installation() {
  root_owned(installation_path, 0600);
  auto config = object(capture(installation_path));
  auto binary = config.at("binary").get<std::string>();
  root_owned(binary);
  auto bytes = capture(binary, 128 * 1024 * 1024);
  require(bytes.size() >= 4 && bytes.substr(0, 4) == std::string("\177ELF", 4),
          "Pinned core must be ELF");
  require(sha256(bytes) == identity(config.value("binarySha256", Json())),
          "Pinned core checksum mismatch");
  return {config, binary};
}
inline void check_binary(const Installation &i,
                         const std::set<std::string> &required) {
  if (!required.empty())
    loaded(object(command({i.binary, "--runtime-capabilities"}).output),
           required);
}
inline Json apply(const std::string &build);
inline std::string restore();
} // namespace stworkbench::hardware

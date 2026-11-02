// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ST Workbench contributors.
#include "stworkbench/common.hpp"
#include <algorithm>
#include <array>
#include <bit>
#include <chrono>
#include <cmath>
#include <cstring>
#include <ctime>
#include <fcntl.h>
#include <functional>
#include <iostream>
#include <map>
#include <memory>
#include <optional>
#include <poll.h>
#include <random>
#include <regex>
#include <set>
#include <signal.h>
#include <span>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>
namespace stworkbench::axis {
using Clock = std::chrono::steady_clock;
using Bytes = std::vector<unsigned char>;
struct ProtocolError : std::runtime_error {
  using runtime_error::runtime_error;
};
struct BackendError : std::runtime_error {
  using runtime_error::runtime_error;
};
struct UnsupportedProof : std::runtime_error {
  using runtime_error::runtime_error;
};
static double now() {
  return std::chrono::duration<double>(Clock::now().time_since_epoch()).count();
}
static uint64_t random64() {
  std::random_device rd;
  return (uint64_t(rd()) << 32 | rd()) | 1;
}
static std::string identity() {
  static constexpr char h[] = "0123456789abcdef";
  auto a = random64(), b = random64();
  std::string s;
  for (int i = 0; i < 32; i++) {
    if (i == 8 || i == 12 || i == 16 || i == 20)
      s += '-';
    s += h[((i < 16 ? a : b) >> ((i % 16) * 4)) & 15];
  }
  s[14] = '4';
  s[19] = h[8 | ((b >> 32) & 3)];
  return s;
}
static bool integer(const Json &j) {
  return j.is_number_integer() && !j.is_boolean();
}
static bool uint(const Json &j) {
  return integer(j) && (j.is_number_unsigned() || j.get<int64_t>() >= 0);
}
static uint64_t u64(const Json &j) {
  if (!uint(j))
    throw ProtocolError("Unsigned integer required");
  return j.get<uint64_t>();
}
static bool yes(const Json &j, const char *k) {
  return j.contains(k) && j[k].is_boolean() && j[k].get<bool>();
}
static bool truth(const Json &j, const char *k) {
  if (!j.contains(k) || j[k].is_null())
    return false;
  auto &v = j[k];
  if (v.is_boolean())
    return v.get<bool>();
  if (v.is_number())
    return v.get<double>() != 0;
  return !v.empty();
}
static double numeric(const Json &j) {
  if (!j.is_number() || j.is_boolean() || !std::isfinite(j.get<double>()))
    throw ProtocolError("Finite numeric required");
  return j.get<double>();
}
static std::string lower(std::string s) {
  for (auto &c : s)
    if (c >= 'A' && c <= 'Z')
      c += 32;
  return s;
}
static bool nonblank(const Json &j) {
  if (!j.is_string())
    return false;
  const auto &text = j.get_ref<const std::string &>();
  size_t count = 0;
  bool content = false;
  // Python's manifest oracle limits Unicode characters, not UTF-8 bytes, and
  // str.strip includes Unicode whitespace. Physical controller identifiers are
  // additionally constrained to ASCII by EtherCatRuntimeCompiler.
  for (size_t i = 0; i < text.size();) {
    auto lead = static_cast<unsigned char>(text[i++]);
    uint32_t code = lead;
    unsigned continuation = 0;
    if (lead >= 0xc2 && lead <= 0xdf) {
      code = lead & 0x1f;
      continuation = 1;
    } else if (lead >= 0xe0 && lead <= 0xef) {
      code = lead & 0xf;
      continuation = 2;
    } else if (lead >= 0xf0 && lead <= 0xf4) {
      code = lead & 7;
      continuation = 3;
    } else if (lead >= 0x80) {
      return false;
    }
    if (i + continuation > text.size())
      return false;
    for (unsigned n = 0; n < continuation; n++) {
      auto byte = static_cast<unsigned char>(text[i++]);
      if ((byte & 0xc0) != 0x80)
        return false;
      code = (code << 6) | (byte & 0x3f);
    }
    if ((continuation == 1 && code < 0x80) ||
        (continuation == 2 && code < 0x800) ||
        (continuation == 3 && code < 0x10000) || code > 0x10ffff ||
        (code >= 0xd800 && code <= 0xdfff) || ++count > 256)
      return false;
    bool whitespace =
        (code >= 9 && code <= 13) || (code >= 0x1c && code <= 0x20) ||
        code == 0x85 || code == 0xa0 || code == 0x1680 ||
        (code >= 0x2000 && code <= 0x200a) || code == 0x2028 ||
        code == 0x2029 || code == 0x202f || code == 0x205f || code == 0x3000;
    content |= !whitespace;
  }
  return count && content;
}
static uint64_t load64(const unsigned char *p) {
  uint64_t n = 0;
  for (int i = 0; i < 8; i++)
    n |= uint64_t(p[i]) << (i * 8);
  return n;
}
static double load_double(const unsigned char *p) {
  return std::bit_cast<double>(load64(p));
}
static void put64(Bytes &b, size_t p, uint64_t n) {
  for (int i = 0; i < 8; i++)
    b[p + i] = n >> (i * 8);
}
static void put_double(Bytes &b, size_t p, double n) {
  put64(b, p, std::bit_cast<uint64_t>(n));
}
static const std::array<const char *, 8> kinds = {
    "", "Enable", "Disable", "Reset", "Home", "MoveAbsolute", "Jog", "Stop"};
static int state(const Json &s) {
  static const std::array<const char *, 8> states = {
      "",         "Disabled",       "Standstill",
      "Homing",   "DiscreteMotion", "ContinuousMotion",
      "Stopping", "ErrorStop"};
  if (!s.contains("state") || !s["state"].is_string())
    return 0;
  for (int i = 1; i <= 7; i++)
    if (s["state"] == states[i])
      return i;
  return 0;
}
static constexpr const char *move_cap = "workbench_axis_move_profile_v2";
static constexpr const char *velocity_cap =
    "workbench_axis_velocity_profile_v2";
// Keep integer token capacity identical to the v4 Python protocol oracle.
// nlohmann otherwise converts an oversized integer to floating point silently.
static Json native_json(std::string_view raw) {
  bool quoted = false, escape = false;
  for (size_t i = 0; i < raw.size(); i++) {
    char c = raw[i];
    if (quoted) {
      if (escape)
        escape = false;
      else if (c == '\\')
        escape = true;
      else if (c == '"')
        quoted = false;
      continue;
    }
    if (c == '"') {
      quoted = true;
      continue;
    }
    if (c == '-' || (c >= '0' && c <= '9')) {
      size_t start = i;
      if (c == '-')
        i++;
      size_t digits = i;
      while (i < raw.size() && raw[i] >= '0' && raw[i] <= '9')
        i++;
      bool real =
          i < raw.size() && (raw[i] == '.' || raw[i] == 'e' || raw[i] == 'E');
      if (!real && i - digits > 20)
        throw ProtocolError("Native integer exceeds wire capacity");
      i = start;
      while (i + 1 < raw.size() &&
             (raw[i + 1] == '+' || raw[i + 1] == '-' || raw[i + 1] == '.' ||
              raw[i + 1] == 'e' || raw[i + 1] == 'E' ||
              (raw[i + 1] >= '0' && raw[i + 1] <= '9')))
        i++;
    }
  }
  return parse_json(raw);
}
struct Manifest {
  Json document, axes;
  std::string digest;
  double period;
  bool v2;
  size_t command_bytes() const { return v2 ? 4112 : 2576; }
  size_t feedback_bytes() const { return v2 ? 6656 : 5632; }
  std::string interface() const {
    return v2 ? "workbench-axis-v2" : "workbench-axis-v1";
  }
  static Manifest from(const Json &d, std::string digest) {
    if (!d.is_object() || !d.contains("SchemaVersion") ||
        !integer(d["SchemaVersion"]) || d["SchemaVersion"] != 1)
      throw ProtocolError("Unsupported manifest schema");
    auto iface = d.value("InterfaceVersion", std::string());
    bool v2 = iface == "workbench-axis-v2";
    if (!v2 && iface != "workbench-axis-v1")
      throw ProtocolError("Unsupported axis interface");
    if (v2) {
      if (!integer(d.at("CommandBytes")) || d["CommandBytes"] != 4112 ||
          !integer(d.at("FeedbackBytes")) || d["FeedbackBytes"] != 6656)
        throw ProtocolError("Axis image size mismatch");
      auto &r = d.at("RequiredRuntimeCapabilities");
      if (!r.is_array() || std::find(r.begin(), r.end(), move_cap) == r.end())
        throw ProtocolError("Explicit v2 capabilities required");
      for (auto &c : r)
        if (c != move_cap && c != velocity_cap)
          throw ProtocolError("Unknown required capability");
    }
    auto &p = d.at("PlcIntervalMilliseconds");
    if (!integer(p) || p < 1 || p > 1000)
      throw ProtocolError("Invalid PLC interval");
    auto &a = d.at("Axes");
    if (!a.is_array() || a.empty() || a.size() > 64)
      throw ProtocolError("Axis count must be 1..64");
    std::set<std::string> names, controllers;
    std::string previous;
    for (size_t i = 0; i < a.size(); i++) {
      auto &x = a[i];
      if (!x.is_object() ||
          (x.contains("Mechanics") && !x["Mechanics"].is_null()))
        throw ProtocolError("Mechanics apply is offline only");
      if (!integer(x.at("Index")) || x["Index"] != i + 1)
        throw ProtocolError("Axes must be dense");
      if (!nonblank(x.at("Name")) || !nonblank(x.at("ControllerAxis")) ||
          !nonblank(x.at("Unit")))
        throw ProtocolError("Axis names and units required");
      std::string n = x["Name"], c = x["ControllerAxis"];
      if (!std::regex_match(n, std::regex("[A-Za-z_][A-Za-z0-9_]*")) ||
          (!previous.empty() && n <= previous) ||
          !names.insert(lower(n)).second ||
          !controllers.insert(lower(c)).second)
        throw ProtocolError("Axes must be unique and ordinal sorted");
      previous = n;
      if (numeric(x.at("MinPosition")) >= numeric(x.at("MaxPosition")))
        throw ProtocolError("Axis bounds must increase");
      for (auto key : {"MaximumVelocity", "MaximumAcceleration",
                       "MaximumDeceleration", "MaximumJerk"}) {
        bool required = v2 && std::string(key) != "MaximumVelocity";
        if (!x.contains(key) || x[key].is_null()) {
          if (required)
            throw ProtocolError("Explicit finite dynamics required");
          continue;
        }
        double v = numeric(x[key]);
        if (v < 0 || (v == 0 && std::string(key) != "MaximumJerk"))
          throw ProtocolError("Positive dynamic cap required");
      }
    }
    return {d, a, std::move(digest), p.get<double>() / 1000., v2};
  }
  static Manifest load(const std::string &path) {
    auto raw = read_file(path, 4 * 1024 * 1024, false);
    return from(parse_json(raw), sha256(raw));
  }
};
struct Backend {
  virtual ~Backend() = default;
  virtual Json request(const std::string &, const Json &,
                       const std::string &expected = "CommandResponse") = 0;
  virtual Json diagnostics() const { return Json::object(); }
  virtual std::string failure() const { return {}; }
};
struct NativeClient : Backend {
  int fd;
  uint64_t sequence = 0;
  std::string failed;
  struct Timing {
    uint64_t count = 0, failures = 0;
    double last = 0, max = 0, sum = 0;
  };
  std::map<std::string, Timing> timings;
  explicit NativeClient(int socket) : fd(socket) {}
  ~NativeClient() {
    if (fd >= 0)
      ::close(fd);
  }
  void abort(std::string reason) {
    if (failed.empty()) {
      failed = std::move(reason);
      if (fd >= 0) {
        ::shutdown(fd, SHUT_RDWR);
        ::close(fd);
        fd = -1;
      }
    }
  }
  void io(void *p, size_t n, bool writing, double deadline) {
    auto *b = static_cast<unsigned char *>(p);
    while (n) {
      double remaining = deadline - now();
      if (remaining <= 0)
        throw ProtocolError("transport_timeout");
      pollfd q{fd, short(writing ? POLLOUT : POLLIN), 0};
      int r = ::poll(&q, 1, std::max(1, int(std::ceil(remaining * 1000))));
      if (r < 0 && errno == EINTR)
        continue;
      if (r == 0)
        throw ProtocolError("transport_timeout");
      if (r < 0)
        throw ProtocolError("transport_io");
      ssize_t k = writing ? ::send(fd, b, n, MSG_NOSIGNAL | MSG_DONTWAIT)
                          : ::recv(fd, b, n, MSG_DONTWAIT);
      if (k < 0 && (errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK))
        continue;
      if (k <= 0)
        throw ProtocolError("transport_io");
      b += k;
      n -= k;
    }
    if (now() > deadline)
      throw ProtocolError("transport_timeout");
  }
  Json request(const std::string &kind, const Json &payload,
               const std::string &expected = "CommandResponse") override {
    if (!failed.empty())
      throw ProtocolError("Native channel requires explicit re-establishment");
    if (sequence == UINT64_MAX) {
      abort("sequence_exhausted");
      throw ProtocolError("Protocol sequence exhausted");
    }
    auto id = identity();
    auto seq = sequence + 1;
    Json envelope = {{"protocolVersion", 4},
                     {"kind", kind},
                     {"requestId", id},
                     {"sequence", seq},
                     {"payload", payload}};
    auto raw = envelope.dump(-1, ' ', false, Json::error_handler_t::strict);
    if (raw.empty() || raw.size() > 1048576)
      throw ProtocolError("Invalid outgoing frame length");
    std::function<void(const Json &)> finite = [&](const Json &j) {
      if (j.is_number_float() && !std::isfinite(j.get<double>()))
        throw ProtocolError("Nonfinite outgoing number");
      if (j.is_structured())
        for (auto &v : j)
          finite(v);
    };
    finite(payload);
    sequence = seq;
    double started = now();
    bool failure_sample = true;
    auto finish = [&] {
      static const std::set<std::string> labels = {
          "HelloRequest",          "ReadSnapshotRequest",
          "AcquireControlRequest", "ReleaseControlRequest",
          "HeartbeatRequest",      "AxisCommandRequest",
          "PlcScanBatchRequest"};
      auto &t = timings[labels.contains(kind) ? kind : "Other"];
      t.count++;
      t.failures += failure_sample;
      t.last = (now() - started) * 1000;
      t.max = std::max(t.max, t.last);
      t.sum += t.last;
    };
    try {
      double deadline = started + .2;
      unsigned char header[4];
      uint32_t len = raw.size();
      for (int i = 0; i < 4; i++)
        header[i] = len >> (8 * i);
      io(header, 4, true, deadline);
      io(raw.data(), raw.size(), true, deadline);
      io(header, 4, false, deadline);
      len = uint32_t(header[0]) | uint32_t(header[1]) << 8 |
            uint32_t(header[2]) << 16 | uint32_t(header[3]) << 24;
      if (!len || len > 1048576)
        throw ProtocolError("invalid_response");
      raw.resize(len);
      io(raw.data(), len, false, deadline);
      auto r = native_json(raw);
      if (now() > deadline)
        throw ProtocolError("transport_timeout");
      if (!r.is_object() || !integer(r.at("protocolVersion")) ||
          r["protocolVersion"] != 4 || r.at("requestId") != id ||
          !uint(r.at("sequence")) || u64(r["sequence"]) != seq ||
          !r.at("payload").is_object())
        throw ProtocolError("invalid_response");
      if (r.at("kind") == "ErrorResponse") {
        auto &c = r["payload"].at("code");
        if (!c.is_string() || c.get_ref<const std::string &>().empty() ||
            c.get_ref<const std::string &>().size() > 128)
          throw ProtocolError("invalid_response");
        throw BackendError(c.get<std::string>());
      }
      if (r.at("kind") != expected ||
          (expected == "CommandResponse" &&
           (!r["payload"].contains("accepted") ||
            !r["payload"]["accepted"].is_boolean())))
        throw ProtocolError("invalid_response");
      failure_sample = false;
      finish();
      return r["payload"];
    } catch (const BackendError &) {
      finish();
      throw;
    } catch (const std::exception &e) {
      std::string reason = e.what();
      abort(reason == "transport_timeout" || reason == "transport_io"
                ? reason
                : "invalid_response");
      finish();
      throw ProtocolError("Native channel failed: " + failed);
    }
  }
  std::string failure() const override { return failed; }
  Json diagnostics() const override {
    Json requests = Json::object();
    for (auto &[k, t] : timings)
      requests[k] = {{"Count", t.count},
                     {"Failures", t.failures},
                     {"LastMilliseconds", t.last},
                     {"MaxMilliseconds", t.max},
                     {"MeanMilliseconds", t.sum / t.count}};
    return {{"State", failed.empty() ? "Connected" : "Disconnected"},
            {"FailureReason", failed.empty() ? Json(nullptr) : Json(failed)},
            {"Requests", requests}};
  }
};
struct Slot {
  uint64_t request = 0, last_seen = 0, rejected_request = 0, rejected_error = 0,
           kind = 0, error = 0, motion_id = 0, applied_cycle = 0;
  bool accepted = false, busy = false, done = false, aborted = false,
       stop_held = false;
  double reset_deadline = 0;
  Json sample = Json::object();
};
struct Worker {
  Manifest manifest;
  Backend &backend;
  std::string mode;
  std::function<double()> time;
  std::array<Slot, 64> slots;
  Json hello = Json::object();
  bool ready = false, motion_ready = false;
  std::string lease, failure_reason;
  std::set<size_t> owned;
  uint64_t failed = 0, scan_index = 0, cycle = 0, session = random64(),
           batch = 0, action = 0, submissions = 0;
  std::optional<double> last_scan;
  double last_sample_change, watchdog, freshness;
  Worker(Manifest m, Backend &b, std::string md,
         std::function<double()> clock = now)
      : manifest(std::move(m)), backend(b), mode(std::move(md)),
        time(std::move(clock)) {
    if (mode != "EcMotion" && mode != "EcMotionSimulator")
      throw ProtocolError("Explicit backend required");
    last_sample_change = time();
    watchdog = std::max(.25, manifest.period * 3);
    freshness = std::max(.5, manifest.period * 3);
  }
  bool physical() const {
    return mode == "EcMotion" && yes(hello, "plcScanBatch") &&
           yes(hello, "plcActionReceipts") &&
           hello.value("plcAbiVersion", Json()) == 3 && ready && !failed;
  }
  bool capability(const char *c) const {
    auto a = hello.value("runtimeCapabilities", Json::array());
    return a.is_array() && std::find(a.begin(), a.end(), c) != a.end();
  }
  Json capabilities() const {
    Json r = {
        {"SchemaVersion", 1},
        {"InterfaceVersion", manifest.interface()},
        {"ManifestSha256", manifest.digest},
        {"Backend", mode},
        {"PID", ::getpid()},
        {"ControllerReady", ready && !failed},
        {"PhysicalBasicAxis", physical()},
        {"SimulatorBasicAxis", mode == "EcMotionSimulator" && ready && !failed},
        {"MotionReady", motion_ready && ready && !failed},
        {"FailureCode", failed},
        {"FailureReason",
         failure_reason.empty() ? Json(nullptr) : Json(failure_reason)},
        {"RecoveryRequired", bool(failed)},
        {"RecoveryAction",
         failed ? "stop_plc_and_reestablish_channel" : "none"},
        {"LinkMetrics", backend.diagnostics()},
        {"Continuous", false},
        {"MotionCycleApplied", false}};
    if (manifest.v2) {
      r["CommandBytes"] = manifest.command_bytes();
      r["FeedbackBytes"] = manifest.feedback_bytes();
      r["RequiredRuntimeCapabilities"] =
          manifest.document["RequiredRuntimeCapabilities"];
      r["NativeRuntimeCapabilities"] =
          hello.value("runtimeCapabilities", Json::array());
    }
    return r;
  }
  void probe() {
    hello = backend.request("HelloRequest",
                            {{"clientName", "Workbench axis worker"},
                             {"clientVersion", manifest.interface()}},
                            "HelloResponse");
    if (!integer(hello.at("protocolVersion")) || hello["protocolVersion"] != 4)
      throw ProtocolError("Hello protocol mismatch");
    if (manifest.v2)
      for (auto &c : manifest.document["RequiredRuntimeCapabilities"])
        if (!capability(c.get_ref<const std::string &>().c_str()))
          throw ProtocolError("Native v2 capability required");
    poll();
    // A correlated but unusable snapshot latches invalid feedback while the
    // private management endpoint remains available, as in both legacy workers.
    // A broken native envelope instead poisons NativeClient; it cannot replay.
  }
  static double timestamp_age(const Json &v) {
    if (!v.is_string())
      throw ProtocolError("Snapshot timestamp required");
    std::string s = v;
    std::smatch m;
    static const std::regex pattern(
        R"(^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(\.[0-9]+)?(Z|[+-][0-9]{2}:[0-9]{2})$)");
    if (!std::regex_match(s, m, pattern))
      throw ProtocolError("Invalid snapshot timestamp");
    std::tm t{};
    t.tm_year = std::stoi(m[1]) - 1900;
    t.tm_mon = std::stoi(m[2]) - 1;
    t.tm_mday = std::stoi(m[3]);
    t.tm_hour = std::stoi(m[4]);
    t.tm_min = std::stoi(m[5]);
    t.tm_sec = std::stoi(m[6]);
    if (t.tm_mon < 0 || t.tm_mon > 11 || t.tm_mday < 1 || t.tm_mday > 31 ||
        t.tm_hour > 23 || t.tm_min > 59 || t.tm_sec > 59)
      throw ProtocolError("Invalid timestamp fields");
    auto original = t;
    auto seconds = timegm(&t);
    if (original.tm_year != t.tm_year || original.tm_mon != t.tm_mon ||
        original.tm_mday != t.tm_mday)
      throw ProtocolError("Invalid calendar date");
    double stamp = double(seconds);
    if (m[7].matched)
      stamp += std::stod(m[7]);
    std::string zone = m[8];
    if (zone != "Z") {
      int hours = std::stoi(zone.substr(1, 2)),
          minutes = std::stoi(zone.substr(4, 2));
      if (hours > 23 || minutes > 59)
        throw ProtocolError("Invalid timestamp offset");
      stamp -= (zone[0] == '+' ? 1 : -1) * (hours * 3600 + minutes * 60);
    }
    return std::chrono::duration<double>(
               std::chrono::system_clock::now().time_since_epoch())
               .count() -
           stamp;
  }
  void poll() {
    try {
      auto snapshot =
          backend.request("ReadSnapshotRequest",
                          {{"includeDiagnostics", false}}, "SnapshotResponse");
      double age = timestamp_age(snapshot.at("timestamp"));
      if (age < -.5 || age > freshness)
        throw ProtocolError("Stale or future snapshot");
      uint64_t c =
          snapshot.contains("sampleCycle") ? u64(snapshot["sampleCycle"]) : 0;
      if (c < cycle)
        throw ProtocolError("Cycle regressed");
      if (mode == "EcMotion") {
        if (!c)
          throw ProtocolError("Missing actual cycle");
        if (c != cycle)
          last_sample_change = time();
        else if (time() - last_sample_change > freshness)
          throw ProtocolError("Actual cycle stalled");
      }
      cycle = c;
      auto &a = snapshot.at("axes");
      if (!a.is_array())
        throw ProtocolError("Snapshot axes must be array");
      std::map<std::string, Json> samples;
      for (auto &s : a) {
        auto n = s.at("name").get<std::string>();
        if (!samples.emplace(n, s).second)
          throw ProtocolError("Duplicate axis");
      }
      ready = yes(snapshot, "runtimeReady") && yes(hello, "runtimeReady");
      motion_ready = !failed && (snapshot.contains("motionReady")
                                     ? yes(snapshot, "motionReady")
                                     : yes(snapshot, "runtimeReady"));
      for (size_t i = 0; i < manifest.axes.size(); i++) {
        std::string name = manifest.axes[i]["ControllerAxis"];
        if (!samples.contains(name))
          throw ProtocolError("Mapped axis absent");
        auto &s = samples.at(name);
        numeric(s.at("actualPosition"));
        double v = numeric(s.at("actualVelocity"));
        int st = state(s);
        if (!st)
          throw ProtocolError("Unknown axis state");
        uint64_t motion = s.contains("motionId") ? u64(s["motionId"]) : 0;
        auto &slot = slots[i];
        slot.sample = s;
        bool fault = truth(s, "fault") || st == 7;
        if (fault)
          motion_ready = false;
        if (!slot.busy || slot.error || slot.aborted)
          continue;
        if (slot.kind == 3) {
          if (!truth(s, "fault") && (st == 1 || st == 2) && v == 0) {
            slot.done = true;
            slot.busy = false;
          } else if (time() >= slot.reset_deadline) {
            slot.error = 2005;
            slot.busy = slot.done = false;
          }
          continue;
        }
        if (fault) {
          slot.error = 2005;
          slot.busy = slot.done = false;
          continue;
        }
        if (slot.kind == 5) {
          if (motion == slot.motion_id && truth(s, "motionAborted")) {
            slot.aborted = true;
            slot.busy = slot.done = false;
          } else if (motion == slot.motion_id && yes(s, "motionDone") &&
                     s.contains("motionBusy") && s["motionBusy"].is_boolean() &&
                     !s["motionBusy"].get<bool>() && st == 2) {
            slot.done = true;
            slot.busy = false;
          }
        } else if (slot.kind != 6) {
          bool terminal = (slot.kind == 1 && st != 1) ||
                          (slot.kind == 2 && st == 1) ||
                          (slot.kind == 4 && yes(s, "homed") && st == 2) ||
                          (slot.kind == 7 && (st == 1 || st == 2) && v == 0);
          bool proof = slot.kind != 4 || mode == "EcMotionSimulator" ||
                       (slot.applied_cycle > 0 && cycle >= slot.applied_cycle);
          if (terminal && proof) {
            slot.done = true;
            slot.busy = false;
          }
        }
      }
    } catch (const std::exception &) {
      shutdown(2004);
    }
  }
  void acquire() {
    if (lease.empty()) {
      auto r = backend.request("AcquireControlRequest",
                               {{"clientName", "Workbench axis worker"},
                                {"requestedLeaseMilliseconds",
                                 std::max(1000, int(watchdog * 2000))}},
                               "AcquireControlResponse");
      if (!r.contains("leaseToken") || !r["leaseToken"].is_string() ||
          r["leaseToken"].get_ref<const std::string &>().empty())
        throw ProtocolError("Missing lease token");
      lease = r["leaseToken"];
    }
  }
  Json direct(size_t i, uint64_t k, double p = 0, double v = 0,
              std::optional<std::array<double, 3>> dynamics = {}) {
    Json payload = {{"leaseToken", lease},
                    {"axis", manifest.axes[i]["ControllerAxis"]},
                    {"command", kinds.at(k)}};
    if (manifest.v2) {
      if (k == 5)
        payload["position"] = p;
      if (k == 5 || k == 6)
        payload["velocity"] = v;
      if (dynamics) {
        payload["motionProfileVersion"] = 2;
        payload["acceleration"] = (*dynamics)[0];
        payload["deceleration"] = (*dynamics)[1];
        payload["jerk"] = (*dynamics)[2];
      }
    } else {
      payload["position"] = k == 5 ? Json(p) : Json(nullptr);
      payload["velocity"] = k == 5 || k == 6 ? Json(v) : Json(nullptr);
    }
    submissions++;
    return backend.request("AxisCommandRequest", payload);
  }
  std::pair<Json, uint64_t> home(size_t i) {
    if (mode == "EcMotionSimulator")
      return {direct(i, 4), 0};
    if (!physical())
      throw UnsupportedProof("Native scan receipts required");
    if (batch == UINT64_MAX || action == UINT64_MAX)
      throw UnsupportedProof("Home sequence exhausted");
    batch++;
    action++;
    Json axes = Json::array();
    for (auto &a : manifest.axes)
      axes.push_back(a["ControllerAxis"]);
    Json a = {{"requestId", action}, {"kind", 3},       {"axis", i},
              {"position", 0.},      {"velocity", 0.},  {"acceleration", 0.},
              {"deceleration", 0.},  {"outputMask", 0}, {"outputValues", 0},
              {"groupCount", 0}};
    submissions++;
    auto r = backend.request("PlcScanBatchRequest",
                             {{"leaseToken", lease},
                              {"sessionId", session},
                              {"scanIndex", batch},
                              {"sourceCycle", cycle},
                              {"deadlineNs", monotonic_ns() + 50000000},
                              {"axes", axes},
                              {"outputs", Json::array()},
                              {"actions", Json::array({a})}});
    auto receipts = r.value("actionReceipts", Json::array());
    if (!receipts.is_array() || receipts.size() != 1 ||
        !uint(receipts[0].value("requestId", Json())) ||
        u64(receipts[0]["requestId"]) != action ||
        !yes(receipts[0], "accepted") ||
        !uint(receipts[0].value("appliedCycle", Json())) ||
        u64(receipts[0]["appliedCycle"]) == 0)
      throw UnsupportedProof("Home AppliedCycle proof required");
    return {r, u64(receipts[0]["appliedCycle"])};
  }
  void reject(size_t i, const Slot &previous, uint64_t request,
              uint64_t error) {
    if (manifest.v2) {
      slots[i] = previous;
      slots[i].last_seen = std::max(request, previous.last_seen);
      slots[i].rejected_request = request;
      slots[i].rejected_error = error;
    } else
      slots[i].error = error;
  }
  void scan(std::span<const unsigned char> data) {
    if (data.size() != manifest.command_bytes())
      throw ProtocolError("Invalid command image length");
    uint64_t scan = load64(data.data()), flags = load64(data.data() + 8);
    if (flags & ~uint64_t(1))
      throw ProtocolError("Unknown scan flags");
    tick();
    if (!(flags & 1)) {
      shutdown(2002);
      return;
    }
    if (scan <= scan_index) {
      if (scan < scan_index)
        shutdown(2002);
      return;
    }
    scan_index = scan;
    last_scan = time();
    if (!lease.empty() && !failed) {
      try {
        if (!yes(backend.request("HeartbeatRequest", {{"leaseToken", lease}}),
                 "accepted"))
          throw BackendError("lease_refused");
      } catch (const std::exception &) {
        shutdown(2003);
      }
    }
    for (size_t i = 0; i < 64; i++) {
      auto *p = data.data() + 16 + i * (manifest.v2 ? 64 : 40);
      uint64_t request = load64(p), k = load64(p + 8),
               sf = load64(p + (manifest.v2 ? 56 : 32));
      double position = load_double(p + 16), velocity = load_double(p + 24);
      auto &old = slots[i];
      if (manifest.v2) {
        if (!request || request == old.last_seen ||
            (request == old.request && old.accepted)) {
          if (request == old.request && old.accepted && old.kind == 7 &&
              !(sf & ~uint64_t(1)))
            old.stop_held = sf & 1;
          continue;
        }
      } else {
        old.stop_held = sf & 1;
        if (!request || request == old.request)
          continue;
      }
      Slot previous = old;
      Slot s;
      s.request = request;
      s.last_seen = request;
      s.kind = k;
      s.sample = previous.sample;
      s.stop_held = previous.stop_held;
      slots[i] = s;
      auto &slot = slots[i];
      auto reject_here = [&](uint64_t e) { reject(i, previous, request, e); };
      if (request < (manifest.v2 ? previous.last_seen : previous.request)) {
        reject_here(2009);
        continue;
      }
      if (i >= manifest.axes.size() || k < 1 || k > 7) {
        reject_here(2001);
        continue;
      }
      if (failed) {
        reject_here(failed);
        continue;
      }
      if ((previous.busy || previous.stop_held) && k != 2 && k != 7) {
        reject_here(2006);
        continue;
      }
      if (sf & ~uint64_t(1) || !std::isfinite(position) ||
          !std::isfinite(velocity)) {
        reject_here(2007);
        continue;
      }
      auto &mapping = manifest.axes[i];
      std::optional<std::array<double, 3>> dynamics;
      if (manifest.v2 && (k == 5 || k == 6 || k == 7)) {
        std::array<double, 3> values = {load_double(p + 32),
                                        load_double(p + 40),
                                        load_double(p + 48)},
                              caps = {numeric(mapping["MaximumAcceleration"]),
                                      numeric(mapping["MaximumDeceleration"]),
                                      numeric(mapping["MaximumJerk"])};
        bool invalid = k == 7 && values[0] != 0;
        for (int d = 0; d < 3; d++)
          if (!std::isfinite(values[d]) || values[d] < 0 || values[d] > caps[d])
            invalid = true;
        if (invalid) {
          reject_here(2007);
          continue;
        }
        if ((k == 6 || k == 7) && !capability(velocity_cap)) {
          reject_here(2008);
          continue;
        }
        for (int d = 0; d < 3; d++)
          if (values[d] == 0)
            values[d] = caps[d];
        dynamics = values;
      }
      if ((k == 5 &&
           (position < numeric(mapping["MinPosition"]) ||
            position > numeric(mapping["MaxPosition"]) || velocity <= 0)) ||
          (k == 6 && velocity == 0) ||
          ((k == 5 || k == 6) && mapping.contains("MaximumVelocity") &&
           !mapping["MaximumVelocity"].is_null() &&
           std::abs(velocity) > numeric(mapping["MaximumVelocity"]))) {
        reject_here(2007);
        continue;
      }
      if ((k == 1 || k == 4 || k == 5 || k == 6) &&
          (truth(slot.sample, "fault") || state(slot.sample) == 7)) {
        reject_here(2005);
        continue;
      }
      if (!ready && k != 2 && k != 3 && k != 7) {
        reject_here(2005);
        continue;
      }
      if (mode == "EcMotion" && !physical() && k != 2 && k != 3 && k != 7) {
        reject_here(2008);
        continue;
      }
      bool previously_owned = owned.contains(i);
      try {
        acquire();
        owned.insert(i);
        auto result = k == 4
                          ? home(i)
                          : std::pair<Json, uint64_t>{
                                direct(i, k, position, velocity, dynamics), 0};
        if (!yes(result.first, "accepted"))
          throw BackendError("command_rejected");
        slot.applied_cycle = result.second;
        if (k == 3)
          slot.reset_deadline = time() + 5;
        if (k == 5) {
          auto m = result.first.value("motionId", Json());
          if (!uint(m) || !u64(m) ||
              (previous.sample.contains("motionId") &&
               m == previous.sample["motionId"]))
            throw UnsupportedProof("Real new MotionId required");
          slot.motion_id = u64(m);
        }
        slot.accepted = true;
        slot.stop_held = sf & 1;
        slot.busy = true;
      } catch (const UnsupportedProof &) {
        slot.error = 2008;
        shutdown(2008);
      } catch (const BackendError &e) {
        std::string code = e.what();
        slot.error = code == "control_busy" || code == "lease_refused" ||
                             code == "invalid_lease" || code == "lease_expired"
                         ? 2003
                         : 2005;
        if (code == "invalid_state" || code == "invalid_argument" ||
            code == "command_rejected") {
          auto error = slot.error;
          reject_here(error);
          if (manifest.v2 && !previously_owned)
            owned.erase(i);
        } else
          shutdown(slot.error);
      } catch (const std::exception &) {
        slot.error = 2004;
        shutdown(2004);
      }
    }
  }
  void tick() {
    if (last_scan && !failed && time() - *last_scan > watchdog)
      shutdown(2002);
  }
  void shutdown(uint64_t error = 2004, bool report = true) {
    if (failed)
      return;
    failure_reason = backend.failure();
    if (failure_reason.empty()) {
      switch (error) {
      case 2002:
        failure_reason = "plc_watchdog_expired";
        break;
      case 2003:
        failure_reason = "control_ownership_lost";
        break;
      case 2004:
        failure_reason = "transport_or_protocol_failed";
        break;
      case 2005:
        failure_reason = "native_command_failed";
        break;
      case 2008:
        failure_reason = "capability_or_receipt_invalid";
        break;
      default:
        failure_reason = "channel_failed";
      }
    }
    failed = error;
    ready = motion_ready = false;
    for (size_t i = 0; i < manifest.axes.size(); i++) {
      auto &s = slots[i];
      if (!s.error)
        s.error = error;
      s.done = false;
    }
    for (auto i : owned) {
      auto &s = slots[i];
      s.done = s.busy = false;
      s.aborted = true;
      if (!s.error)
        s.error = error;
      if (!lease.empty())
        try {
          direct(i, 7);
        } catch (const std::exception &) {
        }
    }
    if (!lease.empty())
      try {
        backend.request("ReleaseControlRequest", {{"leaseToken", lease}});
      } catch (const std::exception &) {
      }
    lease.clear();
    if (report)
      std::cerr << Json({{"event", "axis_channel_failed"},
                         {"interface", manifest.interface()},
                         {"code", failed},
                         {"reason", failure_reason},
                         {"recovery", "stop_plc_and_reestablish_channel"}})
                       .dump()
                << '\n';
  }
  Bytes feedback() const {
    Bytes b(manifest.feedback_bytes(), 0);
    for (size_t i = 0; i < 64; i++) {
      auto &s = slots[i];
      Json sample =
          !failed && i < manifest.axes.size() ? s.sample : Json::object();
      int st = state(sample);
      size_t p = i * (manifest.v2 ? 104 : 88);
      std::array<uint64_t, 9> q = {s.request,
                                   s.busy,
                                   s.done && !failed,
                                   s.error,
                                   s.aborted,
                                   st >= 2 && st <= 6,
                                   st && truth(sample, "homed"),
                                   uint64_t(st),
                                   s.motion_id};
      for (size_t k = 0; k < q.size(); k++)
        put64(b, p + k * 8, q[k]);
      put_double(b, p + 72, sample.value("actualPosition", 0.));
      put_double(b, p + 80, sample.value("actualVelocity", 0.));
      if (manifest.v2) {
        put64(b, p + 88, s.rejected_request);
        put64(b, p + 96, s.rejected_error);
      }
    }
    return b;
  }
};
struct Publisher {
  std::string path;
  Json previous;
  double published = -1e100;
  void operator()(const Json &caps) {
    auto current = caps;
    current.erase("LinkMetrics");
    double n = now();
    if (current == previous && n - published < 1)
      return;
    struct stat info{};
    if (::lstat(path.c_str(), &info) == 0 &&
        (!S_ISREG(info.st_mode) || info.st_uid != ::getuid()))
      throw ProtocolError("Capability target must be an owned regular file");
    atomic_write(path, caps.dump(), 0600);
    previous = std::move(current);
    published = n;
  }
};
static int unix_socket() {
  int fd = ::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
  if (fd < 0)
    throw ProtocolError("Cannot create Unix socket");
  return fd;
}
static sockaddr_un address(const std::string &p) {
  sockaddr_un a{};
  a.sun_family = AF_UNIX;
  if (p.empty() || p.size() >= sizeof(a.sun_path))
    throw ProtocolError("Invalid socket path");
  std::memcpy(a.sun_path, p.c_str(), p.size() + 1);
  return a;
}
static Bytes handshake() {
  Bytes b(24, 0);
  std::memcpy(b.data(), "AXISV2", 6);
  b[8] = 2;
  b[12] = 0x10;
  b[13] = 0x10;
  b[16] = 0;
  b[17] = 0x1a;
  return b;
}
struct Options {
  std::string manifest, socket, controller_socket, backend, capabilities;
};
static volatile sig_atomic_t interrupted = 0;
static void stop_signal(int) { interrupted = 1; }
int serve(const Options &o) {
  if constexpr (std::endian::native != std::endian::little)
    throw ProtocolError("Little-endian Unix required");
  auto m = Manifest::load(o.manifest);
  Publisher publish{o.capabilities.empty() ? o.socket + ".capabilities.json"
                                           : o.capabilities};
  Json initial = {
      {"SchemaVersion", 1},         {"InterfaceVersion", m.interface()},
      {"ManifestSha256", m.digest}, {"Backend", o.backend},
      {"PID", ::getpid()},          {"ControllerReady", false},
      {"PhysicalBasicAxis", false}, {"SimulatorBasicAxis", false},
      {"Continuous", false},        {"MotionCycleApplied", false}};
  if (m.v2) {
    initial["CommandBytes"] = 4112;
    initial["FeedbackBytes"] = 6656;
    initial["RequiredRuntimeCapabilities"] =
        m.document["RequiredRuntimeCapabilities"];
    initial["NativeRuntimeCapabilities"] = Json::array();
  }
  publish(initial);
  auto controller_address = address(o.controller_socket);
  Fd connecting(unix_socket());
  if (::fcntl(connecting.get(), F_SETFL, O_NONBLOCK) < 0)
    throw ProtocolError("Controller socket setup failed");
  int connected = ::connect(connecting.get(),
                            reinterpret_cast<sockaddr *>(&controller_address),
                            sizeof(controller_address));
  if (connected != 0) {
    if (errno != EINPROGRESS && errno != EAGAIN)
      throw ProtocolError("Controller connection failed");
    pollfd event{connecting.get(), POLLOUT, 0};
    if (::poll(&event, 1, 200) <= 0)
      throw ProtocolError("Controller connection deadline expired");
    int error = 0;
    socklen_t length = sizeof(error);
    if (::getsockopt(connecting.get(), SOL_SOCKET, SO_ERROR, &error, &length) <
            0 ||
        error)
      throw ProtocolError("Controller connection failed");
  }
  NativeClient backend(connecting.release());
  auto worker = std::make_unique<Worker>(m, backend, o.backend);
  worker->probe();
  publish(worker->capabilities());
  int server = -1, client = -1;
  bool bound = false, handshaken = !m.v2;
  double handshake_deadline = 0, next_poll = 0;
  Bytes input, output;
  auto cleanup = [&] {
    worker->shutdown(2004,
                     std::any_of(worker->slots.begin(), worker->slots.end(),
                                 [](auto &s) { return s.busy; }));
    try {
      publish(worker->capabilities());
    } catch (...) {
    }
    if (client >= 0)
      ::close(client);
    if (server >= 0)
      ::close(server);
    if (bound)
      ::unlink(o.socket.c_str());
  };
  auto end_session = [&] {
    worker->shutdown(2004,
                     std::any_of(worker->slots.begin(), worker->slots.end(),
                                 [](auto &s) { return s.busy; }));
    publish(worker->capabilities());
    ::close(client);
    client = -1;
    input.clear();
    output.clear();
    handshaken = !m.v2;
    worker = std::make_unique<Worker>(m, backend, o.backend);
    worker->probe();
    publish(worker->capabilities());
  };
  try {
    struct stat info{};
    if (::lstat(o.socket.c_str(), &info) == 0)
      throw ProtocolError("Private socket already exists");
    server = unix_socket();
    auto a = address(o.socket);
    if (::bind(server, reinterpret_cast<sockaddr *>(&a), sizeof(a)) < 0)
      throw ProtocolError("Private socket bind failed");
    bound = true;
    if (::chmod(o.socket.c_str(), 0600) < 0 || ::listen(server, 1) < 0 ||
        ::fcntl(server, F_SETFL, O_NONBLOCK) < 0)
      throw ProtocolError("Private socket setup failed");
    while (!interrupted) {
      double current = now();
      if (client >= 0 && !handshaken && current > handshake_deadline)
        end_session();
      if (current >= next_poll) {
        worker->tick();
        worker->poll();
        publish(worker->capabilities());
        next_poll = current + .01;
      }
      pollfd fds[2] = {
          {server, POLLIN, 0},
          {client, short(POLLIN | (output.empty() ? 0 : POLLOUT)), 0}};
      int r = ::poll(fds, 2, 10);
      if (r < 0) {
        if (errno == EINTR)
          continue;
        throw ProtocolError("Private socket poll failed");
      }
      if (fds[0].revents & POLLIN) {
        int accepted =
            ::accept4(server, nullptr, nullptr, SOCK_NONBLOCK | SOCK_CLOEXEC);
        if (accepted >= 0) {
          if (client >= 0)
            ::close(accepted);
          else {
            client = accepted;
            handshaken = !m.v2;
            handshake_deadline = now() + .2;
            if (m.v2)
              output = handshake();
          }
        }
      }
      if (client < 0)
        continue;
      if (fds[1].revents & (POLLIN | POLLHUP | POLLERR)) {
        Bytes data(4 * m.command_bytes());
        ssize_t n = ::recv(client, data.data(), data.size(), MSG_DONTWAIT);
        if (n == 0 || (n < 0 && errno != EAGAIN && errno != EWOULDBLOCK &&
                       errno != EINTR)) {
          end_session();
          continue;
        }
        if (n > 0) {
          input.insert(input.end(), data.begin(), data.begin() + n);
          if (input.size() > 4 * m.command_bytes())
            throw ProtocolError("Private command capacity exceeded");
          if (!handshaken && input.size() >= 24) {
            auto h = handshake();
            if (!std::equal(h.begin(), h.end(), input.begin())) {
              end_session();
              continue;
            }
            input.erase(input.begin(), input.begin() + 24);
            handshaken = true;
          }
          while (handshaken && input.size() >= m.command_bytes()) {
            auto before = worker->submissions;
            auto failure = worker->failed;
            worker->scan(std::span(input.data(), m.command_bytes()));
            input.erase(input.begin(), input.begin() + m.command_bytes());
            if (worker->submissions != before) {
              worker->poll();
              publish(worker->capabilities());
              next_poll = now() + .01;
            } else if (worker->failed != failure)
              publish(worker->capabilities());
            if (output.size() >= 4 * m.feedback_bytes())
              throw ProtocolError("Private feedback consumer stalled");
            auto fb = worker->feedback();
            output.insert(output.end(), fb.begin(), fb.end());
          }
        }
      }
      if (client >= 0 && !output.empty() && (fds[1].revents & POLLOUT)) {
        ssize_t n = ::send(client, output.data(), output.size(),
                           MSG_NOSIGNAL | MSG_DONTWAIT);
        if (n < 0 && errno != EAGAIN && errno != EWOULDBLOCK &&
            errno != EINTR) {
          end_session();
          continue;
        }
        if (n > 0)
          output.erase(output.begin(), output.begin() + n);
      }
    }
  } catch (...) {
    cleanup();
    throw;
  }
  cleanup();
  return interrupted ? 1 : 0;
}
} // namespace stworkbench::axis
#ifndef ST_AXIS_WORKER_NO_MAIN
int main(int argc, char **argv) {
  using namespace stworkbench::axis;
  Options o;
  try {
    for (int i = 1; i < argc; i++) {
      std::string k = argv[i];
      if (k == "--help") {
        std::cout << "st-axis-worker --manifest FILE --socket PATH "
                     "--controller-socket PATH --backend "
                     "EcMotion|EcMotionSimulator [--capabilities FILE]\n";
        return 0;
      }
      if (i + 1 == argc)
        throw ProtocolError("Missing option value");
      std::string v = argv[++i];
      if (k == "--manifest")
        o.manifest = v;
      else if (k == "--socket")
        o.socket = v;
      else if (k == "--controller-socket")
        o.controller_socket = v;
      else if (k == "--backend")
        o.backend = v;
      else if (k == "--capabilities")
        o.capabilities = v;
      else
        throw ProtocolError("Unknown option");
    }
    if (o.manifest.empty() || o.socket.empty() || o.controller_socket.empty() ||
        (o.backend != "EcMotion" && o.backend != "EcMotionSimulator"))
      throw ProtocolError("Explicit manifest, sockets and backend required");
    ::signal(SIGINT, stop_signal);
    ::signal(SIGTERM, stop_signal);
    ::signal(SIGPIPE, SIG_IGN);
    return serve(o);
  } catch (const std::exception &e) {
    std::cerr << "axis worker: " << e.what() << '\n';
    return 1;
  }
}
#endif

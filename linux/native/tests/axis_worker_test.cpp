// SPDX-License-Identifier: MIT
#define ST_AXIS_WORKER_NO_MAIN
#include "../src/axis_worker.cpp"
#include <atomic>
#include <cassert>
#include <sys/wait.h>
#include <thread>
using namespace stworkbench;
using namespace stworkbench::axis;
static Json document(bool v2 = true) {
  Json d = {
      {"SchemaVersion", 1},
      {"InterfaceVersion", v2 ? "workbench-axis-v2" : "workbench-axis-v1"},
      {"CommandBytes", 4112},
      {"FeedbackBytes", 6656},
      {"PlcIntervalMilliseconds", 10},
      {"RequiredRuntimeCapabilities", Json::array({move_cap})},
      {"Axes", Json::array({{{"Index", 1},
                             {"Name", "A"},
                             {"ControllerAxis", "Axis1"},
                             {"Unit", "mm"},
                             {"MinPosition", -10},
                             {"MaxPosition", 10},
                             {"MaximumVelocity", 5},
                             {"MaximumAcceleration", 2},
                             {"MaximumDeceleration", 3},
                             {"MaximumJerk", 0}}})}};
  return d;
}
static std::string timestamp(double offset = 0) {
  auto point = std::chrono::system_clock::now() +
               std::chrono::milliseconds(int(offset * 1000));
  auto t = std::chrono::system_clock::to_time_t(point);
  std::tm tm{};
  gmtime_r(&t, &tm);
  char s[32];
  strftime(s, sizeof(s), "%Y-%m-%dT%H:%M:%S", &tm);
  auto ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
                point.time_since_epoch())
                .count() %
            1000000000;
  char frac[16];
  snprintf(frac, sizeof(frac), ".%09lldZ", (long long)ns);
  return std::string(s) + frac;
}
struct Fake : Backend {
  Json sample = {
      {"name", "Axis1"},      {"state", "Standstill"}, {"actualPosition", 0.},
      {"actualVelocity", 0.}, {"motionId", 1},         {"homed", true},
      {"fault", false},       {"motionDone", false},   {"motionBusy", false}};
  Json hello = {{"protocolVersion", 4},
                {"runtimeReady", true},
                {"plcScanBatch", true},
                {"plcActionReceipts", true},
                {"plcAbiVersion", 3},
                {"runtimeCapabilities", Json::array({move_cap, velocity_cap})}};
  uint64_t cycle = 10;
  bool freeze = false, ready = true, bad_receipt = false, duplicate = false;
  double age = 0;
  std::string rejection;
  Json last;
  std::vector<std::pair<std::string, Json>> requests;
  Json request(const std::string &k, const Json &p,
               const std::string & = "CommandResponse") override {
    requests.emplace_back(k, p);
    if (k == "HelloRequest")
      return hello;
    if (k == "ReadSnapshotRequest") {
      if (!freeze)
        cycle++;
      Json axes = Json::array({sample});
      if (duplicate)
        axes.push_back(sample);
      return {{"timestamp", timestamp(age)},
              {"sampleCycle", cycle},
              {"runtimeReady", ready},
              {"axes", axes}};
    }
    if (k == "AcquireControlRequest")
      return {{"leaseToken", "private-lease"}};
    if (k == "AxisCommandRequest") {
      last = p;
      if (!rejection.empty())
        throw BackendError(rejection);
      return {{"accepted", true}, {"motionId", 77}};
    }
    if (k == "PlcScanBatchRequest") {
      last = p;
      return {{"accepted", true},
              {"actionReceipts",
               Json::array({{{"requestId", p["actions"][0]["requestId"]},
                             {"accepted", true},
                             {"appliedCycle",
                              bad_receipt ? Json(false) : Json(cycle + 3)}}})}};
    }
    return {{"accepted", true}};
  }
  int count(const std::string &k) const {
    return std::count_if(requests.begin(), requests.end(),
                         [&](auto &p) { return p.first == k; });
  }
};
static Bytes frame(const Manifest &m, uint64_t scan, uint64_t req = 0,
                   uint64_t kind = 0, double pos = 0, double vel = 0,
                   uint64_t flags = 0, std::array<double, 3> dynamics = {}) {
  Bytes b(m.command_bytes());
  put64(b, 0, scan);
  put64(b, 8, 1);
  put64(b, 16, req);
  put64(b, 24, kind);
  put_double(b, 32, pos);
  put_double(b, 40, vel);
  if (m.v2) {
    for (int i = 0; i < 3; i++)
      put_double(b, 48 + i * 8, dynamics[i]);
    put64(b, 72, flags);
  } else
    put64(b, 48, flags);
  return b;
}
static void manifest_tests() {
  auto d = document();
  auto m = Manifest::from(d, "test");
  assert(m.v2 && m.command_bytes() == 4112 && m.feedback_bytes() == 6656);
  auto v1 = Manifest::from(document(false), "test");
  assert(!v1.v2 && v1.command_bytes() == 2576 && v1.feedback_bytes() == 5632);
  {
    auto unicode = d;
    std::string unit;
    // 256 Unicode scalars fit although their UTF-8 representation exceeds 256
    // bytes.
    for (int i = 0; i < 256; i++)
      unit += "米";
    unicode["Axes"][0]["Unit"] = unit;
    Manifest::from(unicode, "unicode");
    unicode["Axes"][0]["ControllerAxis"] = unit;
    Manifest::from(unicode, "unicode-controller");
    unicode["Axes"][0]["Unit"] = unit + "米";
    bool rejected = false;
    try {
      Manifest::from(unicode, "too-long");
    } catch (...) {
      rejected = true;
    }
    assert(rejected);
    unicode = d;
    unicode["Axes"][0]["Unit"] = "\u2003\u3000\u0085";
    rejected = false;
    try {
      Manifest::from(unicode, "blank");
    } catch (...) {
      rejected = true;
    }
    assert(rejected);
  }
  for (auto key : {"MaximumAcceleration", "MaximumDeceleration", "MaximumJerk",
                   "MinPosition", "MaxPosition"}) {
    for (auto val : {Json(true), Json("1"), Json(nullptr)}) {
      auto x = d;
      x["Axes"][0][key] = val;
      bool bad = false;
      try {
        Manifest::from(x, "test");
      } catch (...) {
        bad = true;
      }
      assert(bad);
    }
  }
  for (auto change : {0, 65}) {
    auto x = d;
    x["Axes"][0]["Index"] = change;
    bool bad = false;
    try {
      Manifest::from(x, "test");
    } catch (...) {
      bad = true;
    }
    assert(bad);
  }
  auto x = d;
  x["Axes"][0]["Mechanics"] = Json::object();
  bool bad = false;
  try {
    Manifest::from(x, "test");
  } catch (...) {
    bad = true;
  }
  assert(bad);
}
static void worker_tests() {
  for (bool v2 : {false, true}) {
    auto m = Manifest::from(document(v2), "test");
    Fake b;
    double clock = 0;
    Worker w(m, b, "EcMotion", [&] { return clock; });
    w.probe();
    assert(w.ready && w.physical());
    w.scan(frame(m, 1, 1, 5, 2, 1));
    assert(w.slots[0].busy && w.slots[0].motion_id == 77);
    assert(b.count("AxisCommandRequest") == 1);
    w.scan(frame(m, 2, 1, 5, 2, 1));
    assert(b.count("AxisCommandRequest") == 1);
    b.sample["motionId"] = 77;
    b.sample["motionDone"] = true;
    b.sample["motionBusy"] = false;
    w.poll();
    assert(w.slots[0].done && !w.slots[0].busy);
    auto fb = w.feedback();
    assert(fb.size() == m.feedback_bytes() && load64(fb.data()) == 1 &&
           load64(fb.data() + 16) == 1);
    w.scan(frame(m, 3, 2, 4));
    assert(!w.slots[0].done);
    w.poll();
    assert(!w.slots[0].done);
    w.poll();
    w.poll();
    assert(w.slots[0].done);
    clock = .3;
    w.scan(frame(m, 4, 3, 1));
    assert(w.failed == 2002 &&
           !w.capabilities()["ControllerReady"].get<bool>());
    fb = w.feedback();
    assert(load64(fb.data() + 56) == 0 && load_double(fb.data() + 72) == 0);
    w.poll();
    assert(!w.capabilities()["ControllerReady"].get<bool>());
  }
  auto m = Manifest::from(document(), "test");
  {
    Fake b;
    b.duplicate = true;
    Worker w(m, b, "EcMotion");
    w.probe();
    assert(w.failed == 2004 &&
           !w.capabilities()["ControllerReady"].get<bool>());
    assert(load64(w.feedback().data() + 56) == 0);
  }
  {
    Fake b;
    Worker w(m, b, "EcMotion");
    w.probe();
    assert(b.count("AcquireControlRequest") == 0);
    auto image = frame(m, 1);
    for (size_t i = 0; i < 64; i++) {
      put64(image, 16 + i * 64, UINT64_MAX);
      put64(image, 24 + i * 64, 1);
    }
    w.scan(image);
    for (size_t i = 0; i < 64; i++) {
      assert(w.slots[i].last_seen == UINT64_MAX);
      if (i)
        assert(w.slots[i].rejected_error == 2001);
    }
    w.scan(frame(m, 2, 1, 1));
    assert(w.slots[0].rejected_error == 2009);
  }
  {
    Fake b;
    Worker w(m, b, "EcMotion");
    w.probe();
    w.scan(frame(m, 1, 1, 6, 0, -1));
    w.poll();
    assert(w.slots[0].busy && !w.slots[0].done);
    assert(!b.last.contains("position") && b.last["velocity"] == -1);
  }
  {
    Fake b;
    Worker w(m, b, "EcMotion");
    w.probe();
    w.scan(frame(m, 1, 1, 5, 2, 1));
    b.sample["motionId"] = 76;
    b.sample["motionDone"] = true;
    w.poll();
    assert(!w.slots[0].done);
    b.sample["motionId"] = 77;
    b.sample["motionAborted"] = true;
    w.poll();
    assert(w.slots[0].aborted && !w.slots[0].done);
  }
  {
    Fake b;
    b.sample["fault"] = true;
    b.sample["state"] = "ErrorStop";
    Worker w(m, b, "EcMotion");
    w.probe();
    assert(w.ready && !w.motion_ready);
    w.scan(frame(m, 1, 1, 5, 2, 1));
    assert(!w.failed && w.slots[0].rejected_error == 2005 &&
           b.count("AcquireControlRequest") == 0);
    assert(load64(w.feedback().data() + 56) == 7);
  }
  {
    Fake b;
    Worker w(m, b, "EcMotion");
    w.probe();
    bool bad = false;
    try {
      w.scan(Bytes(2576));
    } catch (const ProtocolError &) {
      bad = true;
    }
    assert(bad);
    auto image = frame(m, 1);
    put64(image, 8, 2);
    bad = false;
    try {
      w.scan(image);
    } catch (const ProtocolError &) {
      bad = true;
    }
    assert(bad);
  }
  {
    Fake b;
    Worker w(m, b, "EcMotion");
    w.probe();
    w.scan(frame(m, 1, 1, 5, 2, 1));
    assert(b.last["acceleration"] == 2 && b.last["deceleration"] == 3 &&
           b.last["jerk"] == 0);
    w.scan(frame(m, 2, 2, 5, 3, 1));
    assert(w.slots[0].request == 1 && w.slots[0].busy &&
           w.slots[0].rejected_request == 2 &&
           w.slots[0].rejected_error == 2006);
    w.scan(frame(m, 3, 2, 7));
    assert(w.slots[0].kind == 5 && b.count("AxisCommandRequest") == 1);
    w.scan(frame(m, 4, 3, 7, 0, 0, 1));
    w.poll();
    assert(w.slots[0].done && w.slots[0].stop_held);
    w.scan(frame(m, 5, 4, 5, 2, 1));
    assert(w.slots[0].request == 3 && w.slots[0].stop_held);
    w.scan(frame(m, 6, 4, 7, 0, 0, 0));
    assert(w.slots[0].stop_held);
    w.scan(frame(m, 7, 3, 7, 0, 0, 0));
    assert(!w.slots[0].stop_held);
    w.scan(frame(m, 8, 5, 5, 2, 1));
    assert(w.slots[0].request == 5 && w.slots[0].busy);
    auto fb = w.feedback();
    assert(load64(fb.data() + 88) == 0);
  }
  for (auto k : {5, 6, 7})
    for (auto dyn :
         {std::array<double, 3>{3, 0, 0}, std::array<double, 3>{0, 4, 0},
          std::array<double, 3>{0, 0, 1}, std::array<double, 3>{0, -1, 0},
          std::array<double, 3>{0, std::numeric_limits<double>::infinity(),
                                0}}) {
      Fake b;
      Worker w(m, b, "EcMotion");
      w.probe();
      w.scan(frame(m, 1, 1, k, 1, 1, 0, dyn));
      assert(w.slots[0].rejected_error == 2007 &&
             b.count("AxisCommandRequest") == 0);
    }
  {
    Fake b;
    b.hello["runtimeCapabilities"] = Json::array({move_cap});
    Worker w(m, b, "EcMotion");
    w.probe();
    w.scan(frame(m, 1, 1, 6, 0, 1));
    assert(w.slots[0].rejected_error == 2008 && !w.failed);
  }
  {
    Fake b;
    b.bad_receipt = true;
    Worker w(m, b, "EcMotion");
    w.probe();
    w.scan(frame(m, 1, 1, 4));
    assert(w.failed == 2008 && w.slots[0].aborted);
  }
  {
    Fake b;
    Worker w(m, b, "EcMotion");
    w.probe();
    b.rejection = "invalid_state";
    w.scan(frame(m, 1, 1, 5, 2, 1));
    assert(!w.failed && w.owned.empty() && w.slots[0].rejected_error == 2005);
    b.rejection.clear();
    w.scan(frame(m, 2, 2, 5, 2, 1));
    assert(w.slots[0].busy);
    b.rejection = "invalid_argument";
    w.scan(frame(m, 3, 3, 7));
    assert(!w.failed && w.slots[0].busy && w.slots[0].request == 2 &&
           w.owned.contains(0));
  }
  {
    Fake b;
    double clock = 0;
    Worker w(m, b, "EcMotion", [&] { return clock; });
    w.probe();
    b.sample["fault"] = true;
    b.sample["state"] = "ErrorStop";
    w.poll();
    w.scan(frame(m, 1, 1, 3));
    w.poll();
    assert(w.slots[0].busy && !w.slots[0].error);
    b.sample["fault"] = false;
    b.sample["state"] = "Disabled";
    w.poll();
    assert(w.slots[0].done);
  }
  {
    Fake b;
    double clock = 0;
    Worker w(m, b, "EcMotion", [&] { return clock; });
    w.probe();
    b.sample["fault"] = true;
    b.sample["state"] = "ErrorStop";
    w.poll();
    w.scan(frame(m, 1, 1, 3));
    clock = 5.1;
    w.poll();
    assert(w.slots[0].error == 2005 && !w.slots[0].done);
  }
  for (int invalid = 0; invalid < 4; invalid++) {
    Fake b;
    double clock = 0;
    Worker w(m, b, "EcMotion", [&] { return clock; });
    w.probe();
    if (invalid == 0)
      b.age = -2;
    else if (invalid == 1)
      b.age = 2;
    else if (invalid == 2)
      b.duplicate = true;
    else {
      b.freeze = true;
      clock = 1;
    }
    w.poll();
    assert(w.failed == 2004);
  }
  {
    Fake b;
    Worker first(m, b, "EcMotion");
    first.probe();
    first.scan(frame(m, 1, 1, 5, 2, 1));
    first.shutdown(2004, false);
    Worker second(m, b, "EcMotion");
    second.probe();
    assert(second.session != first.session && second.owned.empty() &&
           second.scan_index == 0 && second.slots[0].request == 0);
    second.scan(frame(m, 1, 1, 1));
    assert(second.slots[0].request == 1 && second.slots[0].accepted);
  }
}
static void exact_io(int fd, void *p, size_t n, bool send) {
  auto *b = (unsigned char *)p;
  while (n) {
    ssize_t k = send ? ::write(fd, b, n) : ::read(fd, b, n);
    assert(k > 0);
    b += k;
    n -= k;
  }
}
static Json receive_json(int fd) {
  unsigned char h[4];
  exact_io(fd, h, 4, false);
  uint32_t n = uint32_t(h[0]) | uint32_t(h[1]) << 8 | uint32_t(h[2]) << 16 |
               uint32_t(h[3]) << 24;
  assert(n > 0 && n < 1048576);
  std::string s(n, ' ');
  exact_io(fd, s.data(), n, false);
  return parse_json(s);
}
static void send_raw(int fd, const std::string &s) {
  unsigned char h[4];
  for (int i = 0; i < 4; i++)
    h[i] = s.size() >> (8 * i);
  exact_io(fd, h, 4, true);
  exact_io(fd, (void *)s.data(), s.size(), true);
}
static Json response(const Json &r, const Json &p,
                     const std::string &k = "CommandResponse") {
  return {{"protocolVersion", 4},
          {"kind", k},
          {"requestId", r["requestId"]},
          {"sequence", r["sequence"]},
          {"payload", p}};
}
static void protocol_tests() {
  {
    bool bad = false;
    try {
      native_json("{\"n\":100000000000000000000}");
    } catch (...) {
      bad = true;
    }
    assert(bad);
    assert(native_json("{\"n\":\"100000000000000000000\"}")["n"].is_string());
    int f[2];
    assert(!socketpair(AF_UNIX, SOCK_STREAM, 0, f));
    NativeClient client(f[0]);
    try {
      client.request("Unknown",
                     {{"n", std::numeric_limits<double>::infinity()}});
      assert(false);
    } catch (const ProtocolError &) {
    }
    assert(client.sequence == 0 && client.failure().empty());
    ::close(f[1]);
  }
  for (int corruption = 0; corruption < 7; corruption++) {
    int f[2];
    assert(!socketpair(AF_UNIX, SOCK_STREAM, 0, f));
    NativeClient client(f[0]);
    std::thread server([&] {
      auto r = receive_json(f[1]);
      auto reply = response(r, {{"accepted", true}});
      if (corruption == 0)
        reply["sequence"] = true;
      if (corruption == 1)
        reply["requestId"] = "different";
      if (corruption == 2)
        reply["protocolVersion"] = true;
      if (corruption == 3)
        reply["payload"]["accepted"] = 1;
      if (corruption == 4)
        reply["kind"] = "SnapshotResponse";
      std::string raw = reply.dump();
      if (corruption == 5)
        raw.insert(1, "\"protocolVersion\":4,");
      if (corruption == 6)
        raw = "{\"value\":NaN}";
      send_raw(f[1], raw);
      ::close(f[1]);
    });
    bool threw = false;
    try {
      client.request("AxisCommandRequest", Json::object());
    } catch (const ProtocolError &) {
      threw = true;
    }
    assert(threw && !client.failure().empty());
    auto seq = client.sequence;
    try {
      client.request("ReadSnapshotRequest", Json::object());
    } catch (...) {
    }
    assert(client.sequence == seq);
    server.join();
  }
  {
    int f[2];
    assert(!socketpair(AF_UNIX, SOCK_STREAM, 0, f));
    NativeClient client(f[0]);
    std::thread server([&] {
      auto r = receive_json(f[1]);
      send_raw(
          f[1],
          response(r, {{"code", "invalid_state"}}, "ErrorResponse").dump());
      r = receive_json(f[1]);
      send_raw(f[1], response(r, {{"accepted", true}}).dump());
      ::close(f[1]);
    });
    bool rejected = false;
    try {
      client.request("AxisCommandRequest", Json::object());
    } catch (const BackendError &) {
      rejected = true;
    }
    assert(rejected && client.failure().empty());
    assert(yes(client.request("HeartbeatRequest", Json::object()), "accepted"));
    server.join();
  }
  {
    int f[2];
    assert(!socketpair(AF_UNIX, SOCK_STREAM, 0, f));
    NativeClient client(f[0]);
    std::thread server([&] {
      receive_json(f[1]);
      std::this_thread::sleep_for(std::chrono::milliseconds(240));
      ::close(f[1]);
    });
    double start = now();
    try {
      client.request("HelloRequest", Json::object());
      assert(false);
    } catch (const ProtocolError &) {
    }
    assert(client.failure() == "transport_timeout" && now() - start < .4);
    server.join();
  }
  {
    int f[2];
    assert(!socketpair(AF_UNIX, SOCK_STREAM, 0, f));
    NativeClient client(f[0]);
    std::thread server([&] {
      for (int i = 0; i < 12; i++) {
        auto r = receive_json(f[1]);
        send_raw(f[1], response(r, {{"accepted", true}}).dump());
      }
      ::close(f[1]);
    });
    for (int i = 0; i < 12; i++)
      client.request("untrusted-label-" + std::to_string(i), Json::object());
    assert(client.diagnostics()["Requests"].size() == 1 &&
           client.diagnostics()["Requests"]["Other"]["Count"] == 12);
    server.join();
  }
  {
    int f[2];
    assert(!socketpair(AF_UNIX, SOCK_STREAM, 0, f));
    NativeClient client(f[0]);
    std::thread server([&] {
      auto r = receive_json(f[1]);
      auto s = response(r, {{"accepted", true}}).dump();
      unsigned char h[4];
      for (int i = 0; i < 4; i++)
        h[i] = s.size() >> (i * 8);
      exact_io(f[1], h, 4, true);
      for (char c : s) {
        std::this_thread::sleep_for(std::chrono::milliseconds(15));
        if (::send(f[1], &c, 1, MSG_NOSIGNAL) <= 0)
          break;
      }
      ::close(f[1]);
    });
    double start = now();
    try {
      client.request("HelloRequest", Json::object());
      assert(false);
    } catch (const ProtocolError &) {
    }
    assert(client.failure() == "transport_timeout" && now() - start < .4);
    server.join();
  }
}
static void publisher_tests() {
  char folder[] = "/tmp/st-axis-publisher-XXXXXX";
  assert(mkdtemp(folder));
  std::string root = folder, path = root + "/capabilities.json";
  Publisher publish{path};
  Json state = {{"ControllerReady", true}, {"LinkMetrics", {{"Requests", 1}}}};
  publish(state);
  auto first = read_file(path);
  state["LinkMetrics"]["Requests"] = 2;
  publish(state);
  assert(read_file(path) == first);
  state["ControllerReady"] = false;
  publish(state);
  assert(!parse_json(read_file(path))["ControllerReady"].get<bool>());
  std::string target = root + "/target", link = root + "/link";
  atomic_write(target, "untouched");
  assert(!symlink(target.c_str(), link.c_str()));
  bool rejected = false;
  try {
    Publisher unsafe{link};
    unsafe(state);
  } catch (...) {
    rejected = true;
  }
  assert(rejected && read_file(target) == "untouched");
  auto manifest = root + "/manifest.json";
  atomic_write(manifest, document().dump());
  atomic_write(path, Json({{"ControllerReady", true}}).dump());
  rejected = false;
  try {
    serve({manifest, root + "/axis.sock", root + "/absent.sock", "EcMotion",
           path});
  } catch (...) {
    rejected = true;
  }
  assert(rejected &&
         !parse_json(read_file(path))["ControllerReady"].get<bool>());
  std::filesystem::remove_all(root);
}
static void lifecycle_test(bool v2) {
  char folder[] = "/tmp/st-axis-test-XXXXXX";
  assert(mkdtemp(folder));
  std::string root = folder, controller = root + "/controller.sock",
              socket = root + "/axis.sock", manifest = root + "/manifest.json",
              caps = root + "/caps.json";
  atomic_write(manifest, document(v2).dump());
  int listener = unix_socket();
  auto addr = address(controller);
  assert(!::bind(listener, reinterpret_cast<sockaddr *>(&addr), sizeof(addr)));
  assert(!::listen(listener, 1));
  pid_t child = fork();
  assert(child >= 0);
  if (!child) {
    ::close(listener);
    ::signal(SIGTERM, stop_signal);
    ::signal(SIGPIPE, SIG_IGN);
    int result = 1;
    try {
      result = serve({manifest, socket, controller, "EcMotion", caps});
    } catch (const std::exception &e) {
      std::cerr << e.what() << '\n';
    }
    _exit(result);
  }
  std::atomic<unsigned> hello_count = 0;
  std::atomic<unsigned> commands = 0;
  std::thread backend([&] {
    int fd = accept(listener, nullptr, nullptr);
    assert(fd >= 0);
    Fake fake;
    for (;;) {
      pollfd q{fd, POLLIN, 0};
      if (::poll(&q, 1, 3000) <= 0)
        break;
      unsigned char peek;
      ssize_t n = ::recv(fd, &peek, 1, MSG_PEEK);
      if (n <= 0)
        break;
      auto r = receive_json(fd);
      std::string kind = r["kind"];
      if (kind == "HelloRequest")
        hello_count++;
      if (kind == "AxisCommandRequest")
        commands++;
      auto p = fake.request(kind, r["payload"]);
      std::string expected =
          kind == "HelloRequest"            ? "HelloResponse"
          : kind == "ReadSnapshotRequest"   ? "SnapshotResponse"
          : kind == "AcquireControlRequest" ? "AcquireControlResponse"
                                            : "CommandResponse";
      send_raw(fd, response(r, p, expected).dump());
    }
    ::close(fd);
  });
  for (int i = 0; i < 300 && !std::filesystem::exists(socket); i++)
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
  assert(std::filesystem::exists(socket));
  auto connect_plc = [&] {
    int fd = unix_socket();
    auto a = address(socket);
    assert(!::connect(fd, reinterpret_cast<sockaddr *>(&a), sizeof(a)));
    timeval timeout{2, 0};
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
    if (v2) {
      Bytes h(24);
      exact_io(fd, h.data(), h.size(), false);
      assert(h == handshake());
      exact_io(fd, h.data(), h.size(), true);
    }
    return fd;
  };
  // A loader's short probe ends a PLC session; the service retains its physical
  // native transport.
  int plc = connect_plc();
  ::close(plc);
  for (int i = 0; i < 200 && hello_count < 2; i++)
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
  assert(hello_count == 2);
  auto m = Manifest::from(document(v2), "test");
  plc = connect_plc();
  auto command = frame(m, 1, 1, 5, 2, 1);
  exact_io(plc, command.data(), command.size(), true);
  Bytes feedback(m.feedback_bytes());
  exact_io(plc, feedback.data(), feedback.size(), false);
  assert(load64(feedback.data()) == 1 && load64(feedback.data() + 8) == 1);
  unsigned before = commands;
  exact_io(plc, command.data(), command.size(), true);
  exact_io(plc, feedback.data(), feedback.size(), false);
  assert(commands == before);
  ::close(plc);
  for (int i = 0; i < 200 && hello_count < 3; i++)
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
  assert(hello_count == 3);
  plc = connect_plc();
  command = frame(m, 1, 1, 1);
  exact_io(plc, command.data(), command.size(), true);
  exact_io(plc, feedback.data(), feedback.size(), false);
  assert(load64(feedback.data()) == 1);
  ::close(plc);
  auto capability = parse_json(read_file(caps));
  assert(capability["InterfaceVersion"] == m.interface());
  struct stat info{};
  assert(!stat(caps.c_str(), &info) && (info.st_mode & 0777) == 0600);
  assert(!stat(socket.c_str(), &info) && (info.st_mode & 0777) == 0600);
  ::kill(child, SIGTERM);
  int status;
  assert(waitpid(child, &status, 0) == child);
  backend.join();
  ::close(listener);
  assert(!std::filesystem::exists(socket));
  std::filesystem::remove_all(root);
}
int main() {
  manifest_tests();
  worker_tests();
  protocol_tests();
  publisher_tests();
  lifecycle_test(false);
  lifecycle_test(true);
  std::cout << "axis worker tests passed\n";
}

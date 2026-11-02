// SPDX-License-Identifier: MIT
#include "../src/axis_diagnostics.hpp"
#include <cassert>
using namespace stworkbench::diagnostics;
using stworkbench::Json;
int main() {
  auto rejects = [](auto f) {
    bool bad = false;
    try {
      f();
    } catch (...) {
      bad = true;
    }
    assert(bad);
  };
  rejects([] { probe_options(1, 10); });
  rejects([] { probe_options(1000, 1000); });
  Json policy = {
      {"axes", Json::array({{{"name", "Axis1"},
                             {"motionCompletion",
                              {{"positionTolerance", .0001},
                               {"velocityTolerance", .0005},
                               {"dwellMilliseconds", 100},
                               {"positionStabilityCompletion", true}}}}})}};
  StationaryProof proof(policy);
  auto sample = [](int n) {
    return Json{{"runtimeReady", true},
                {"ioReady", true},
                {"outputsAtConfiguredSafeState", true},
                {"sampleCycle", n},
                {"sampleMonotonicNs", 1000000000LL + n * 50000000LL},
                {"axes", Json::array({{{"name", "Axis1"},
                                       {"state", "ErrorStop"},
                                       {"fault", "latched fault"},
                                       {"motionBusy", false},
                                       {"driveStatusword", 8},
                                       {"actualPosition", 0.},
                                       {"actualVelocity", .015}}})}};
  };
  assert(!proof.observe(sample(1), 1050000000LL));
  assert(!proof.observe(sample(2), 1100000000LL));
  assert(proof.observe(sample(3), 1150000000LL));
  for (auto mode : {"enabled", "busy", "stale", "future", "moving", "frozen"}) {
    StationaryProof p(policy);
    auto first = sample(1);
    p.observe(first, 1050000000LL);
    auto bad = sample(2);
    long long now = 1100000000LL;
    if (std::string(mode) == "enabled")
      bad["axes"][0]["driveStatusword"] = 39;
    if (std::string(mode) == "busy")
      bad["axes"][0]["motionBusy"] = true;
    if (std::string(mode) == "stale")
      now += 1000000000LL;
    if (std::string(mode) == "future")
      now -= 1000000000LL;
    if (std::string(mode) == "moving")
      bad["axes"][0]["actualPosition"] = .001;
    if (std::string(mode) == "frozen")
      bad = first;
    rejects([&] { p.observe(bad, now); });
  }
  int requests = 0, snapshots = 0;
  std::int64_t clock = 1000000000LL;
  auto call = [&](const std::string &kind, const Json &, const std::string &) {
    ++requests;
    if (kind == "HelloRequest")
      return Json{{"protocolVersion", 4}};
    assert(kind == "ReadSnapshotRequest");
    ++snapshots;
    clock += 1000000;
    return Json{{"sampleCycle", snapshots},
                {"sampleMonotonicNs", clock},
                {"runtimeReady", true},
                {"ioReady", true},
                {"motionReady", false},
                {"axes", Json::array({{{"fault", "latched fault"}}})}};
  };
  auto report = measure(call, 3, 1, [](int) {}, [&] { return clock; });
  assert(requests == 4 && snapshots == 3 && report["Status"] == "PASS" &&
         report["AxisFaultCount"] == 1 && report["MotionReady"] == false);
  requests = 0;
  rejects([&] { measure(call, 1, 1); });
  assert(requests == 0);
  auto frozen = [](const std::string &kind, const Json &, const std::string &) {
    if (kind == "HelloRequest")
      return Json{{"protocolVersion", 4}};
    return Json{{"sampleCycle", 1},
                {"sampleMonotonicNs", 1000000000LL},
                {"axes", Json::array()}};
  };
  rejects(
      [&] { measure(frozen, 3, 1, [](int) {}, [] { return 1000000000LL; }); });
}

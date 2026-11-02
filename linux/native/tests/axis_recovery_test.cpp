// SPDX-License-Identifier: MIT
#include "../src/axis_recovery.cpp"
#include <cassert>
using stworkbench::Json;
struct Fake final : stworkbench::recovery::Coordinator {
  int stops = 0, resumes = 0, admissions = 0, pid_calls = 0;
  bool running = false, changed = false, dirty = false;
  Json active() override { return {{"build_hash", std::string(64, 'a')}}; }
  void verify_active(const Json &, const std::string &) override {}
  std::string plc_status() override { return running ? "RUNNING" : "STOPPED"; }
  Json transaction() override {
    return dirty ? Json{{"state", "prepared"}} : Json(nullptr);
  }
  std::int64_t native_core_pid() override {
    return changed && pid_calls++ ? 18 : 17;
  }
  Json admit_resources(const Json &) override {
    ++admissions;
    return {
        {"axes", Json::array({{{"name", "A"},
                               {"motionCompletion",
                                {{"positionTolerance", .001},
                                 {"velocityTolerance", .01},
                                 {"dwellMilliseconds", 1},
                                 {"positionStabilityCompletion", true}}}}})}};
  }
  Json snapshot() override {
    static std::uint64_t cycle = 0;
    return {{"runtimeReady", true},
            {"ioReady", true},
            {"outputsAtConfiguredSafeState", true},
            {"sampleCycle", ++cycle},
            {"sampleMonotonicNs", stworkbench::monotonic_ns()},
            {"axes", Json::array({{{"name", "A"},
                                   {"state", "ErrorStop"},
                                   {"motionBusy", false},
                                   {"driveStatusword", 8},
                                   {"actualPosition", 0.},
                                   {"actualVelocity", 0.}}})}};
  }
  void stop_axis_bridge() override { ++stops; }
  void resume_axis_bridge() override { ++resumes; }
  void verify_start_hardware(const Json &) override {}
};
int main() {
  auto rejects = [](auto fn) {
    bool bad = false;
    try {
      fn();
    } catch (...) {
      bad = true;
    }
    assert(bad);
  };
  Fake running;
  running.running = true;
  rejects([&] {
    stworkbench::recovery::recover_active(running, std::string(64, 'a'));
  });
  assert(!running.stops && !running.admissions);
  Fake dirty;
  dirty.dirty = true;
  rejects([&] {
    stworkbench::recovery::recover_active(dirty, std::string(64, 'a'));
  });
  assert(!dirty.stops && !dirty.admissions);
  Fake changed;
  changed.changed = true;
  rejects([&] {
    stworkbench::recovery::recover_active(changed, std::string(64, 'a'));
  });
  assert(!changed.stops && !changed.resumes);
  Fake good;
  auto r = stworkbench::recovery::recover_active(good, std::string(64, 'a'));
  assert(good.stops == 1 && good.resumes == 1 &&
         r["motion_restarted"] == false);
}

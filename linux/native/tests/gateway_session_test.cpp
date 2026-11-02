// SPDX-License-Identifier: MIT
#define ST_GATEWAY_NO_MAIN
#include "../src/motion_gateway.cpp"
#include <cassert>
using namespace stworkbench;
using namespace stworkbench::axis;
static std::string timestamp(double offset = 0) {
  auto point = std::chrono::system_clock::now() +
               std::chrono::milliseconds(int(offset * 1000));
  auto t = std::chrono::system_clock::to_time_t(point);
  std::tm tm{};
  gmtime_r(&t, &tm);
  char text[64];
  strftime(text, sizeof(text), "%Y-%m-%dT%H:%M:%S", &tm);
  auto ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
                point.time_since_epoch())
                .count() %
            1000000000;
  char fraction[24];
  snprintf(fraction, sizeof(fraction), ".%09lldZ", (long long)ns);
  return std::string(text) + fraction;
}
struct Fake : Backend {
  uint64_t cycle = 1;
  unsigned commands = 0, stops = 0;
  Json homed = true;
  Json request(const std::string &kind, const Json &p,
               const std::string &expected = "CommandResponse") override {
    (void)expected;
    if (kind == "HelloRequest")
      return {{"protocolVersion", 4}, {"runtimeReady", true}};
    if (kind == "ReadSnapshotRequest")
      return {{"timestamp", timestamp()},
              {"sampleCycle", ++cycle},
              {"runtimeReady", true},
              {"axes", Json::array({{{"name", "Axis1"},
                                     {"state", "Standstill"},
                                     {"actualPosition", 0.},
                                     {"actualVelocity", 0.},
                                     {"motionId", 1},
                                     {"homed", homed},
                                     {"fault", false},
                                     {"motionDone", false},
                                     {"motionBusy", false}}})}};
    if (kind == "AcquireControlRequest")
      return {{"leaseToken", "token"},
              {"grantedLeaseMilliseconds", 500},
              {"expiresAt", timestamp(.5)}};
    if (kind == "AxisCommandRequest") {
      ++commands;
      if (p["command"] == "Stop")
        ++stops;
      return {{"accepted", true}, {"motionId", 77}};
    }
    return {{"accepted", true}};
  }
};
int main() {
  Json m = {{"SchemaVersion", 1},
            {"InterfaceVersion", "workbench-axis-v1"},
            {"PlcIntervalMilliseconds", 10},
            {"Axes", Json::array({{{"Index", 1},
                                   {"Name", "A"},
                                   {"ControllerAxis", "Axis1"},
                                   {"Unit", "mm"},
                                   {"MinPosition", -10},
                                   {"MaxPosition", 10},
                                   {"MaximumVelocity", 5}}})}};
  Fake backend;
  gateway::Gateway g(Manifest::from(m, "digest"), backend, "EcMotionSimulator");
  auto call = [&](Json r) { return g.handle(r); };
  assert(call({{"requestId", "acquire"},
               {"action", "acquire"},
               {"durationMilliseconds", 500}})["success"] == true);
  auto move = Json{{"requestId", "move"},    {"action", "execute"},
                   {"operationId", "op"},    {"axis", "A"},
                   {"kind", "MoveAbsolute"}, {"position", 1},
                   {"velocity", 1}};
  assert(call(move)["state"] == "Accepted");
  assert(backend.commands == 1);
  assert(call(move)["success"] == false);
  move["requestId"] = "duplicate-operation";
  assert(call(move)["success"] == false);
  assert(backend.commands == 1);
  assert(call({{"requestId", "query"},
               {"action", "operation"},
               {"operationId", "op"}})["success"] == true);
  g.close(true);
  assert(backend.stops == 1);
  move["requestId"] = "closed";
  move["operationId"] = "never";
  assert(call(move)["success"] == false);
  assert(backend.commands == 2);
  assert(call({{"requestId", "unknown"},
               {"action", "operation"},
               {"operationId", "op"}})["state"] == "Unknown");
  Fake malformed;
  gateway::Gateway checked(Manifest::from(m, "digest"), malformed,
                           "EcMotionSimulator");
  malformed.homed = 1;
  checked.tick();
  assert(checked.closed);
}

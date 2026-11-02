// SPDX-License-Identifier: MIT
#define ST_PATH_NO_MAIN
#include "../src/path_worker.cpp"
#include <cassert>
int main() {
  using namespace stworkbench;
  Json sample = {{"TimeSeconds", 0.0},
                 {"Position", {{"X", 0}, {"Y", 0}, {"Z", 0}}},
                 {"Velocity", {{"X", 0}, {"Y", 0}, {"Z", 0}}},
                 {"Acceleration", {{"X", 0}, {"Y", 0}, {"Z", 0}}}};
  Json second = sample;
  second["TimeSeconds"] = .01;
  Json asset = {{"Id", "p"},          {"NumericId", 1},
                {"Version", 1},       {"ContentHash", std::string(64, 'a')},
                {"Axes", {"X", "Y"}}, {"Mode", "Continuous"},
                {"Unit", "mm"},       {"Profile", {{"CycleSeconds", .01}}}};
  Json m = {{"SchemaVersion", 1},
            {"Backend", "VirtualContinuous"},
            {"Paths",
             Json::array({{{"Asset", asset},
                           {"Trajectory",
                            {{"DurationSeconds", .01},
                             {"Samples", Json::array({sample, second})}}}}})}};
  Engine e(m, .25);
  Command c{};
  c.request = c.id = c.version = c.scan = c.flags = 1;
  memset(c.digest, 'a', 64);
  assert(e.command(3, c, 1).busy);
  assert(e.command(4, c, 1).error == 1004);
  e.tick(1.02);
  assert(e.state.done);
  c.digest[0] = 'b';
  assert(e.command(3, c, 1.03).error == 1007);
  e.release(3);
  c.digest[0] = 'a';
  assert(e.command(4, c, 2).busy);
  e.tick(2.3);
  assert(e.state.error == 1002);
  m["Backend"] = "PhysicalContinuous";
  bool rejected = false;
  try {
    Engine bad(m, .25);
  } catch (...) {
    rejected = true;
  }
  assert(rejected);
}

// SPDX-License-Identifier: MIT
#include "../src/hardware_operations.hpp"
#include <cassert>
using namespace stworkbench::hardware;
using stworkbench::Json;
using stworkbench::atomic_write;
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
  auto base = fs::temp_directory_path() /
              ("st-native-admission-" + std::to_string(::getpid()));
  fs::create_directory(base);
  struct Cleanup {
    fs::path p;
    ~Cleanup() {
      std::error_code e;
      fs::remove_all(p, e);
    }
  } cleanup{base};
  atomic_write(base / "good", "{}", 0600);
  assert(capture(base / "good", 2) == "{}");
  rejects([&] { capture(base / "good", 1); });
  ::symlink((base / "good").c_str(), (base / "link").c_str());
  rejects([&] { capture(base / "link"); });
  fs::create_directory(base / "real");
  atomic_write(base / "real" / "file", "{}");
  ::symlink((base / "real").c_str(), (base / "parent").c_str());
  rejects([&] { capture(base / "parent" / "file"); });
  ::mkfifo((base / "fifo").c_str(), 0600);
  rejects([&] { capture(base / "fifo"); });
  rejects([&] { capture(base / "good", 32, -1, 0644); });
  auto move = "workbench_axis_move_profile_v2";
  Json m = {{"AxisInterfaceVersion", "workbench-axis-v2"},
            {"AxisExportMarker", "workbench_axis_v2"},
            {"AxisCommandBytes", 4112},
            {"AxisFeedbackBytes", 6656},
            {"RequiredRuntimeCapabilities", Json::array({move})}};
  auto r = requirements(Json{{"requiredRuntimeCapabilities",
                              Json::array({"workbench_motion_migration_v1"})}},
                        m);
  assert(r.size() == 2);
  rejects(
      [&] { loaded(Json{{"runtimeCapabilities", Json::array({move})}}, r); });
  loaded(Json{{"runtimeCapabilities",
               Json::array({move, "workbench_motion_migration_v1"})}},
         r);
  rejects([&] {
    auto x = m;
    x["AxisCommandBytes"] = true;
    requirements(Json::object(), x);
  });
  rejects([&] {
    trace(Json{{"axisTrace", {{"enabled", true}, {"capacity", 120001}}}},
          std::string(64, 'a'));
  });
}

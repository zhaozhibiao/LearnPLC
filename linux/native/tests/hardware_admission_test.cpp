// SPDX-License-Identifier: MIT
#include "../src/hardware_private.hpp"
#include <cassert>
using namespace stworkbench::hardware;
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
  rejects([] { identity("../x"); });
  rejects([] {
    request(Json{
        {"protocolVersion", true}, {"requestId", "x"}, {"action", "status"}});
  });
  rejects([] {
    request(Json{{"protocolVersion", 1},
                 {"requestId", "x"},
                 {"action", "apply"},
                 {"buildHash", std::string(64, 'a')},
                 {"path", "/tmp/x"}});
  });
  rejects([] {
    requirements(Json::object(),
                 Json{{"AxisInterfaceVersion", "workbench-axis-v2"}});
  });
  rejects([] {
    requirements(Json::object(), Json{{"RequiredRuntimeCapabilities",
                                       Json::array({"future"})}});
  });
  rejects([] {
    disabled(Json{{"runtimeReady", true},
                  {"ioReady", true},
                  {"outputsAtConfiguredSafeState", true},
                  {"axes", Json::array({{{"state", "Disabled"},
                                         {"fault", nullptr},
                                         {"driveStatusword", 39}}})}},
             false);
  });
  rejects([] {
    disabled(Json{{"runtimeReady", true},
                  {"ioReady", true},
                  {"outputsAtConfiguredSafeState", true},
                  {"axes", Json::array()},
                  {"lastAppliedCommandSequence", 1}},
             true);
  });
  rejects([] { root_owned("/tmp"); });
  assert(requirements(Json::object(), Json::object()).empty());
}

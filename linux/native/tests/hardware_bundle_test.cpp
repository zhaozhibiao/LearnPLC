// SPDX-License-Identifier: MIT
#include "../src/hardware_operations.hpp"
#include <cassert>
using stworkbench::Json;
using namespace stworkbench::hardware;
int main() {
  auto rejects = [](auto action) {
    bool bad = false;
    try {
      action();
    } catch (...) {
      bad = true;
    }
    assert(bad);
  };
  std::array<std::string, 4> bytes;
  bytes[1] = Json{{"requiredRuntimeCapabilities",
                   Json::array({"workbench_motion_migration_v1"})}}
                 .dump();
  bytes[2] = "{}";
  Json topology = {{"schemaVersion", 1},
                   {"readyForRuntime", true},
                   {"blockers", Json::array()},
                   {"machineSha256", stworkbench::sha256(bytes[1])},
                   {"ioMapSha256", stworkbench::sha256(bytes[2])},
                   {"slaves", Json::array({Json{{"position", 0}}})}};
  auto fingerprint = stworkbench::sha256(topology.dump());
  topology["topologyFingerprint"] = fingerprint;
  bytes[0] = topology.dump();
  std::string build(64, 'a');
  Json inventory = Json::object();
  for (int n = 0; n < 3; ++n)
    inventory[files[n]] = stworkbench::sha256(bytes[n]);
  bytes[3] = Json{{"BuildHash", build}, {"Files", inventory}}.dump();
  auto bundle = verify(bytes, build);
  assert(bundle.proof["topologyFingerprint"] == fingerprint);
  assert(bundle.required.contains("workbench_motion_migration_v1"));
  rejects([&] {
    auto changed = bytes;
    changed[1] += " ";
    verify(changed, build);
  });
  rejects([&] { verify(bytes, std::string(64, 'b')); });
  rejects([&] {
    auto changed = bytes;
    auto j = object(changed[3]);
    j["Files"][files[0]] = std::string(64, '0');
    changed[3] = j.dump();
    verify(changed, build);
  });
  rejects([&] {
    auto changed = bytes;
    auto t = topology;
    t["blockers"] = Json::array({"unsafe"});
    t.erase("topologyFingerprint");
    t["topologyFingerprint"] = stworkbench::sha256(t.dump());
    changed[0] = t.dump();
    auto m = object(changed[3]);
    m["Files"][files[0]] = stworkbench::sha256(changed[0]);
    changed[3] = m.dump();
    verify(changed, build);
  });
  rejects([&] {
    auto changed = bytes;
    auto m = object(changed[3]);
    m["AxisInterfaceVersion"] = "workbench-axis-v2";
    changed[3] = m.dump();
    verify(changed, build);
  });
}

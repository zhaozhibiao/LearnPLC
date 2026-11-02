// SPDX-License-Identifier: MIT
#define ST_CPU_ISOLATION_NO_MAIN
#include "../src/cpu_isolation.cpp"
#include <cassert>
int main() {
  using namespace stworkbench::hardware;
  auto targets = cpu_targets();
  assert(targets.size() == 5);
  auto unit =
      targets.at("/etc/systemd/system/st-workbench-cpu-isolation.service");
  assert(
      unit.find(
          "ExecStart=/usr/local/libexec/st-workbench-cpu-isolation --boot") !=
      unit.npos);
  for (auto &[name, body] : targets) {
    assert(name.starts_with("/etc/"));
    assert(body.find("python") == body.npos);
  }
  assert(trim(" \n2,6\t") == "2,6");
  assert(trim(" \t\n").empty());
  assert(targets.at("/etc/default/grub.d/90-st-workbench-isolation.cfg")
             .find(cpu_params) != std::string::npos);
}

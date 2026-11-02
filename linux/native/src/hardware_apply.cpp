// SPDX-License-Identifier: MIT
#include "hardware_operations.hpp"
#ifndef ST_HARDWARE_APPLY_NO_MAIN
int main(int argc, char **argv) {
  using namespace stworkbench;
  try {
    hardware::require(argc == 2, "Exactly one 64-hex build argument required");
    auto r = hardware::apply(argv[1]);
    std::cout << r.dump() << '\n';
    return r.value("status", "") == "applied" ? 0 : 1;
  } catch (const std::exception &e) {
    std::cout << Json{{"status", "rejected"},
                      {"error", std::string(e.what()).substr(0, 512)}}
                     .dump()
              << '\n';
    return 1;
  }
}
#endif

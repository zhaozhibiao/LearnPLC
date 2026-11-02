// SPDX-License-Identifier: MIT
#include "hardware_operations.hpp"
int main(int argc, char **) {
  try {
    stworkbench::hardware::require(argc == 1,
                                   "Root restore takes no arguments");
    std::cout << stworkbench::hardware::restore() << '\n';
    return 0;
  } catch (const std::exception &e) {
    std::cerr << e.what() << '\n';
    return 1;
  }
}

// SPDX-License-Identifier: MIT
#include "stworkbench/startup_gate.hpp"
#include <iostream>
int main(int argc, char **argv) {
  try {
    if (argc != 1)
      throw std::runtime_error("Startup gate takes no arguments");
    stworkbench::validate_startup(
        "/home/a/.local/share/st-workbench/runtime-v4.3.0");
    return 0;
  } catch (const std::exception &error) {
    std::cerr << "Runtime startup refused: " << error.what() << '\n';
    return 1;
  }
}

// SPDX-License-Identifier: MIT
#include "axis_diagnostics.hpp"
int main(int argc, char **argv) {
  using namespace stworkbench::diagnostics;
  try {
    std::string endpoint = "/run/st-workbench-is620n/controller.sock";
    int samples = 100, interval = 10;
    for (int i = 1; i < argc; ++i) {
      std::string arg = argv[i];
      require(i + 1 < argc, "Missing probe option value");
      std::string value = argv[++i];
      if (arg == "--controller-socket")
        endpoint = value;
      else {
        require(!value.empty() &&
                    value.find_first_not_of("0123456789") == value.npos,
                "Invalid numeric probe option");
        auto n = std::stoul(value);
        require(n <= 1000, "Probe option exceeds capacity");
        if (arg == "--samples")
          samples = int(n);
        else if (arg == "--interval-ms")
          interval = int(n);
        else
          throw std::runtime_error("Unknown probe option");
      }
    }
    probe_options(samples, interval);
    ReadonlyNativeClient client(endpoint);
    auto result = measure(
        [&](auto &kind, auto &payload, auto &expected) {
          return client.request(kind, payload, expected);
        },
        samples, interval);
    std::cout << result.dump() << '\n';
    return 0;
  } catch (const std::exception &e) {
    std::cerr << e.what() << '\n';
    return 1;
  }
}

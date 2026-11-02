// SPDX-License-Identifier: MIT
#include "deployment_recovery.hpp"
#include <iostream>
int main(int argc, char **argv) {
  umask(0077);
  stworkbench::Json request;
  try {
    auto config = stworkbench::parse_json(stworkbench::read_file(
        "/home/a/st-workbench-deployment/config.json", 65536, true));
    stworkbench::deployment::require(
        config.value("prefix", stworkbench::Json{}) ==
                "/home/a/.local/share/st-workbench/runtime-v4.3.0/deployment" &&
            config.value("runtime_source", stworkbench::Json{}) ==
                "/home/a/.local/share/st-workbench/runtime-v4.3.0/source",
        "Production configuration must identify exact private Runtime");
    stworkbench::deployment::Coordinator coordinator(config);
    if (argc > 1) {
      auto mode = std::string(argv[1]);
      if (mode == "--start-active-workers" && argc == 2) {
        coordinator.start_active_workers();
        return 0;
      }
      if (mode == "--stop-workers" && argc == 2) {
        coordinator.stop_workers();
        return 0;
      }
      stworkbench::deployment::require(
          argc == 3 &&
              (mode == "--bind-legacy-active" || mode == "--recover-axis"),
          "Unknown coordinator CLI mode");
      stworkbench::Fd lock(::open((coordinator.prefix / "owner.lock").c_str(),
                                  O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW,
                                  0600));
      stworkbench::deployment::require(
          bool(lock) && ::flock(lock.get(), LOCK_EX | LOCK_NB) == 0,
          "Another deployment owns instance");
      stworkbench::Json result;
      if (mode == "--bind-legacy-active")
        result = coordinator.bind_legacy(argv[2]);
      else {
        stworkbench::deployment::RecoveryAdapter adapter(coordinator);
        result = stworkbench::recovery::recover_active(adapter, argv[2]);
      }
      result["success"] = true;
      std::cout << result.dump() << '\n';
      return 0;
    }
    std::string raw;
    char b[65536];
    while (std::cin) {
      std::cin.read(b, sizeof b);
      raw.append(b, std::cin.gcount());
      stworkbench::deployment::require(raw.size() <= 64 * 1024 * 1024,
                                       "Request capacity exceeded");
    }
    request = stworkbench::parse_json(raw);
    auto result = coordinator.execute(request);
    std::cout << result.dump() << '\n';
    return result.value("success", false) ? 0 : 1;
  } catch (const std::exception &e) {
    std::cout << stworkbench::Json(
                     {{"success", false},
                      {"state", "failed-or-unknown"},
                      {"request_id",
                       request.is_object()
                           ? request.value("request_id", stworkbench::Json{})
                           : stworkbench::Json{}},
                      {"message", e.what()}})
                     .dump()
              << '\n';
    return 1;
  }
}

// SPDX-License-Identifier: MIT
#include "stworkbench/startup_gate.hpp"
#include <cassert>
using namespace stworkbench;
int main() {
  namespace fs = std::filesystem;
  const auto root =
      fs::temp_directory_path() / ("st-gate-" + std::to_string(getpid()));
  fs::create_directories(root / "source/build");
  fs::create_directory(root / "deployment");
  struct Cleanup {
    fs::path p;
    ~Cleanup() { fs::remove_all(p); }
  } cleanup{root};
  auto rejects = [&](auto f) {
    bool bad = false;
    try {
      f();
    } catch (...) {
      bad = true;
    }
    assert(bad);
  };
  validate_startup(root);
  const auto library = root / "source/build/libplc_stworkbench.so";
  atomic_write(library, "test-library");
  rejects([&] { validate_startup(root); });
  Json active = {{"runtime_commit", RuntimeRevision},
                 {"build_hash", "test"},
                 {"library_path", library.string()},
                 {"library_sha256", sha256("test-library")}};
  atomic_write(root / "deployment/active.json", active.dump());
  validate_startup(root);
  atomic_write(root / "source/build/libplc_extra.so.old", "stale");
  rejects([&] { validate_startup(root); });
  fs::remove(root / "source/build/libplc_extra.so.old");
  atomic_write(root / "deployment/transaction.json",
               Json{{"state", "activating"}}.dump());
  rejects([&] { validate_startup(root); });
  atomic_write(root / "deployment/transaction.json",
               Json{{"state", "committed"}, {"new_active", active}}.dump());
  validate_startup(root);
  atomic_write(library, "tampered");
  rejects([&] { validate_startup(root); });
  atomic_write(library, "test-library");
  fs::rename(library, root / "original");
  fs::create_symlink(root / "original", library);
  rejects([&] { validate_startup(root); });
}

// SPDX-License-Identifier: MIT
#include "../src/deployment_coordinator.hpp"
#include <cassert>
using namespace stworkbench;
using namespace stworkbench::deployment;
int main(int argc, char **argv) {
  if (argc == 4) {
    auto b = read_bundle(argv[1], argv[2], argv[3]);
    validate_dependencies(b);
    std::cout << Json({{"state", "validated"},
                       {"build_hash", b.manifest.at("BuildHash")},
                       {"native_source_bundle_sha256",
                        b.manifest.at("NativeSourceBundleSha256")}})
                     .dump()
              << '\n';
    return 0;
  }
  auto rejects = [](auto f) {
    bool bad = false;
    try {
      f();
    } catch (...) {
      bad = true;
    }
    assert(bad);
  };
  assert(base64_decode("YWJj") == "abc");
  assert(base64_decode("YQ==") == "a");
  assert(base64_decode("YWI=") == "ab");
  for (auto s : {"YQ=", "YQ=a", "YR==", "YWJ=", "ab\n="})
    rejects([&] { base64_decode(s); });
  const auto root = fs::temp_directory_path() /
                    ("coordinator-test-" + std::to_string(getpid()));
  fs::create_directories(root / "runtime/build");
  fs::create_directories(root / "deployment");
  struct Cleanup {
    fs::path p;
    ~Cleanup() {
      std::error_code e;
      fs::remove_all(p, e);
    }
  } cleanup{root};
  Json config = {{"prefix", (root / "deployment").string()},
                 {"runtime_source", (root / "runtime").string()},
                 {"runtime_commit", RuntimeCommit},
                 {"exclusive_control", true},
                 {"control_transport", "rest"}};
  Coordinator c(config);
  Json active = {
      {"build_hash", nullptr},
      {"library_path", (root / "runtime/build/libplc_stworkbench.so").string()},
      {"library_sha256", nullptr}};
  c.verify_active(active, Json{});
  atomic_write(root / "runtime/build/libplc_extra.so.old", "stale");
  rejects([&] { c.verify_active(active, Json{}); });
  fs::remove(root / "runtime/build/libplc_extra.so.old");
  atomic_write(root / "runtime/build/libplc_stworkbench.so", "library");
  active["build_hash"] = std::string(64, 'A');
  active["library_sha256"] = sha256("library");
  c.verify_active(active, active["build_hash"]);
  rejects([&] { c.verify_active(active, std::string(64, 'B')); });
  atomic_write(root / "runtime/build/libplc_stworkbench.so", "changed");
  rejects([&] { c.verify_active(active, active["build_hash"]); });
  save_json(root / "deployment/transaction.json", {{"state", "prepared"}});
  rejects([&] {
    c.execute({{"action", "compile"},
               {"request_id", "test"},
               {"build_hash", std::string(64, 'A')}});
  });
  rejects([&] { c.execute({{"action", "status"}, {"request_id", "../bad"}}); });
  std::string entry = "extern \"C\" strucpp::ConfigurationInstance* "
                      "strucpp_get_config(void) {\n    return &g_config;\n}";
  auto hooked =
      candidate_source("core/strucpp_runtime/runtime_v4_entry.cpp", entry,
                       {{"AxisInterfaceVersion", "workbench-axis-v2"}});
  assert(hooked.find("workbench_axis_bootstrap") != hooked.npos);
  rejects([&] {
    candidate_source("core/strucpp_runtime/runtime_v4_entry.cpp", "changed",
                     {{"AxisInterfaceVersion", "workbench-axis-v2"}});
  });
  std::cout << "deployment identity, transaction exclusion, source hooks and "
               "base64 passed\n";
}

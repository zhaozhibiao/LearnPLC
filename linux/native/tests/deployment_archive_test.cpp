// SPDX-License-Identifier: MIT
#include "../src/deployment_archive.hpp"
#include <cassert>
using namespace stworkbench;
using namespace stworkbench::deployment;
using namespace std::string_literals;
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
  assert(identity(std::string(64, 'a')) == std::string(64, 'A'));
  rejects([] { identity("../x"); });
  for (auto name : {"/tmp/a", "a/../b", "a\\b", "a//b", "a/./b", "a:b", "a/"})
    rejects([&] { safe_member(name); });
  safe_member("native/include/stworkbench/common.hpp");
  rejects([]{zip_limits("not-a-zip");});
  std::map<std::string, std::string> files = {
      {"native/CMakeLists.txt", "native"}};
  Json manifest = {
      {"NativeWorkerInterfaceVersion", "workbench-linux-native-v1"},
      {"NativeSources", {{"native/CMakeLists.txt", sha256("native")}}},
      {"NativeWorkerTargets", Json::array({"st-axis-worker"})}};
  manifest["NativeSourceBundleSha256"] =
      sha256("native/CMakeLists.txt\0"s + sha256("native") + "\n");
  native_sources(manifest, files);
  files["native/unlisted.hpp"]="extra";
  rejects([&] { native_sources(manifest, files); });
  files.erase("native/unlisted.hpp");
  manifest["NativeSources"]["native/CMakeLists.txt"] = std::string(64, '0');
  rejects([&] { native_sources(manifest, files); });
}

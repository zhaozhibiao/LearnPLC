// SPDX-License-Identifier: MIT
#pragma once
#include "common.hpp"
#include <regex>

namespace stworkbench {
inline constexpr char RuntimeRevision[] =
    "a9e38d901fb87deb5a8cf778b52639b9387834d0";
inline void validate_startup(const std::filesystem::path &prefix) {
  namespace fs = std::filesystem;
  const auto deployment = prefix / "deployment";
  const auto build = prefix / "source/build";
  auto require = [](bool pass, const char *message) {
    if (!pass)
      throw std::runtime_error(message);
  };
  // Check every existing ancestor, including dangling links.
  for (auto path : {deployment, build}) {
    for (auto p = fs::absolute(path); !p.empty(); p = p.parent_path()) {
      require(!fs::is_symlink(fs::symlink_status(p)),
              "Startup directory contains a symlink");
      if (p == p.root_path())
        break;
    }
  }
  auto present = [](const fs::path &p) {
    return fs::exists(p) || fs::is_symlink(fs::symlink_status(p));
  };
  auto record = [&](const fs::path &path) {
    auto value = parse_json(read_file(path, 1024 * 1024));
    require(value.is_object(), "Deployment record must be an object");
    return value;
  };
  std::vector<fs::path> libraries;
  for (const auto &entry : fs::directory_iterator(build)) {
    const auto name = entry.path().filename().string();
    if (name.starts_with("libplc_") && name.find(".so") != std::string::npos)
      libraries.push_back(entry.path());
  }
  Json active;
  if (present(deployment / "active.json"))
    active = record(deployment / "active.json");
  if (present(deployment / "transaction.json")) {
    const auto transaction = record(deployment / "transaction.json");
    const auto state = transaction.value("state", Json{});
    require(state == "committed" || state == "rolled-back",
            "Unfinished deployment transaction blocks startup");
    const auto key = state == "committed" ? "new_active" : "old_active";
    require(!active.is_null() && transaction.contains(key) &&
                active == transaction[key],
            "Deployment transaction does not match active identity");
  }
  if (active.is_null()) {
    require(libraries.empty(), "Unapproved program library blocks startup");
    return;
  }
  require(active.value("runtime_commit", Json{}) == RuntimeRevision,
          "Active Runtime revision differs");
  const auto path = active.value("library_path", Json{});
  const auto digest = active.value("library_sha256", Json{});
  if (active.value("build_hash", Json{}).is_null() && digest.is_null()) {
    require(path.is_null() ||
                path == (build / "libplc_stworkbench.so").string(),
            "Empty active identity has an unexpected target");
    require(libraries.empty(),
            "Empty active identity contains a program library");
    return;
  }
  require(path.is_string() && digest.is_string(),
          "Active artifact identity is incomplete");
  const auto hash = digest.get<std::string>();
  require(std::regex_match(hash, std::regex("[0-9a-f]{64}")),
          "Invalid active artifact digest");
  const fs::path library(path.get<std::string>());
  require(library.is_absolute() &&
              library.parent_path().lexically_normal() ==
                  fs::absolute(build).lexically_normal() &&
              std::regex_match(library.filename().string(),
                               std::regex("libplc_[A-Za-z0-9_.-]+\\.so")),
          "Active library is outside reviewed build directory");
  require(libraries.size() == 1 && libraries.front().lexically_normal() ==
                                       library.lexically_normal(),
          "Active library set differs from committed manifest");
  require(sha256(read_file(library, 100 * 1024 * 1024)) == hash,
          "Active library checksum differs from committed identity");
}
} // namespace stworkbench

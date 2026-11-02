// SPDX-License-Identifier: MIT
#pragma once
#include "stworkbench/common.hpp"
#include <algorithm>
#include <archive.h>
#include <archive_entry.h>
#include <map>
#include <memory>
#include <regex>
#include <set>
namespace stworkbench::deployment {
namespace fs = std::filesystem;
inline constexpr char RuntimeCommit[] =
    "a9e38d901fb87deb5a8cf778b52639b9387834d0";
inline void require(bool good, const std::string &message) {
  if (!good)
    throw std::runtime_error(message);
}
inline std::string lower(std::string value) {
  for (auto &c : value)
    c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
  return value;
}
inline std::string identity(const Json &value) {
  require(value.is_string(), "SHA256 identity required");
  auto s = value.get<std::string>();
  require(std::regex_match(s, std::regex("[0-9a-fA-F]{64}")),
          "Invalid build identity");
  for (auto &c : s)
    c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
  return s;
}
inline void safe_member(const std::string &name) {
  require(!name.empty() && name.size() <= 1024 && name.front() != '/' &&
              name.back() != '/' && name.find_first_of("\\:") == name.npos &&
              name.find('\0') == name.npos,
          "Unsafe archive member");
  fs::path p(name);
  require(p.generic_string() == name, "Noncanonical archive path");
  for (auto &part : p)
    require(part != "." && part != ".." && !part.empty(),
            "Unsafe archive path traversal");
}
inline Json read_json(const fs::path &p, std::size_t limit = 4 * 1024 * 1024) {
  auto j = parse_json(read_file(p, limit));
  require(j.is_object(), "Expected JSON object");
  return j;
}
inline void save_json(const fs::path &p, const Json &j) {
  fs::create_directories(p.parent_path());
  atomic_write(p, j.dump(2) + "\n");
}
inline std::string file_hash(const fs::path &p) {
  return sha256(read_file(p, 128 * 1024 * 1024));
}
using Contents = std::map<std::string, std::string>;
inline std::string native_sources(const Json &manifest, const Contents &files) {
  require(manifest.value("NativeWorkerInterfaceVersion", Json{}) ==
              "workbench-linux-native-v1",
          "Native worker interface required");
  const auto sources = manifest.value("NativeSources", Json{}),
             targets = manifest.value("NativeWorkerTargets", Json{});
  require(sources.is_object() && !sources.empty() && sources.size() <= 128 &&
              targets.is_array() && !targets.empty(),
          "Native source inventory missing");
  require(sources.contains("native/CMakeLists.txt"),"Native build recipe missing");
  for(auto &[name,body]:files)if(name.starts_with("native/"))require(sources.contains(name),"Native file missing from source bundle identity");
  std::string canonical;
  std::set<std::string> seen;
  for (auto i = sources.begin(); i != sources.end(); ++i) {
    safe_member(i.key());
    require(i.key().starts_with("native/") && files.contains(i.key()),
            "Native source path missing");
    auto hash = lower(identity(i.value()));
    require(sha256(files.at(i.key())) == hash,
            "Native source checksum differs");
    canonical += i.key();
    canonical += '\0';
    canonical += hash;
    canonical += '\n';
  }
  for (auto &t : targets) {
    require(t.is_string(), "Native target must be string");
    auto s = t.get<std::string>();
    require((s == "st-axis-worker" || s == "st-communication-worker" ||
             s == "st-path-worker" || s == "st-motion-gateway") &&
                seen.insert(s).second,
            "Unsupported or duplicate native target");
  }
  const auto hash = sha256(canonical);
  require(lower(identity(manifest.value("NativeSourceBundleSha256", Json{}))) ==
              hash,
          "Native source bundle identity differs");
  return hash;
}
inline std::string topology(const Contents &files) {
  auto t = parse_json(files.at("ethercat-runtime.json"));
  require(t.is_object() && t.value("schemaVersion", Json{}) == 1 &&
              t.value("readyForRuntime", Json{}) == true &&
              t.value("blockers", Json{}) == Json::array(),
          "EtherCAT topology has unresolved runtime blockers");
  auto fingerprint = lower(identity(t.value("topologyFingerprint", Json{})));
  t.erase("topologyFingerprint");
  require(sha256(t.dump()) == fingerprint,
          "Topology canonical identity mismatch");
  require(lower(identity(t.value("machineSha256", Json{}))) ==
                  sha256(files.at("machine-config.json")) &&
              lower(identity(t.value("ioMapSha256", Json{}))) ==
                  sha256(files.at("io-map.json")),
          "Hardware companion hash mismatch");
  require(t.contains("slaves") && t["slaves"].is_array() &&
              !t["slaves"].empty() && t["slaves"].size() <= 64,
          "Invalid EtherCAT slave topology");
  return fingerprint;
}
struct Bundle {
  Json manifest;
  Contents files;
};
inline void zip_limits(std::string_view raw) {
  auto u16 = [&](std::size_t at) {
    require(at + 2 <= raw.size(), "Truncated ZIP metadata");
    return uint32_t(uint8_t(raw[at])) | (uint32_t(uint8_t(raw[at + 1])) << 8);
  };
  auto u32 = [&](std::size_t at) {
    require(at + 4 <= raw.size(), "Truncated ZIP metadata");
    return uint32_t(uint8_t(raw[at])) | (uint32_t(uint8_t(raw[at + 1])) << 8) |
           (uint32_t(uint8_t(raw[at + 2])) << 16) |
           (uint32_t(uint8_t(raw[at + 3])) << 24);
  };
  require(raw.size() >= 22, "ZIP end record missing");
  std::size_t end = raw.size() - 22;
  bool found = false;
  for (;;) {
    if (u32(end) == 0x06054b50 && end + 22 + u16(end + 20) == raw.size()) {
      found = true;
      break;
    }
    if (!end || raw.size() - end > 65557)
      break;
    --end;
  }
  require(found && u16(end + 4) == 0 && u16(end + 6) == 0 &&
              u16(end + 8) == u16(end + 10),
          "Unsupported ZIP volume/end record");
  auto count = u16(end + 10);
  std::size_t offset = u32(end + 16), bytes = u32(end + 12);
  require(count > 0 && count <= 1024 && offset <= end && bytes == end - offset,
          "ZIP directory capacity/layout invalid");
  std::size_t position = offset, total = 0;
  for (std::size_t i = 0; i < count; ++i) {
    require(position + 46 <= end && u32(position) == 0x02014b50,
            "Invalid ZIP central directory");
    auto compressed = u32(position + 20), expanded = u32(position + 24);
    require(expanded <= 16 * 1024 * 1024 &&
                uint64_t(expanded) <=
                    uint64_t(std::max(1u, compressed)) * 500 &&
                total + expanded <= 128 * 1024 * 1024,
            "ZIP compression/expanded capacity exceeded");
    total += expanded;
    auto name = u16(position + 28), extra = u16(position + 30),
         comment = u16(position + 32);
    require(u16(position + 34) == 0 &&
                position + 46 + name + extra + comment <= end,
            "Invalid ZIP member volume/length");
    safe_member(std::string(raw.substr(position + 46, name)));
    position += 46 + name + extra + comment;
  }
  require(position == end, "ZIP central directory size differs");
}
inline void validate_contents(Bundle &bundle, const std::string &build,
                              bool hardware) {
  auto &files = bundle.files;
  require(files.contains("build-manifest.json"), "Build manifest missing");
  require(files.at("build-manifest.json").size() <= 1024 * 1024,
          "Build manifest exceeds capacity");
  auto &m = bundle.manifest;
  m = parse_json(files.at("build-manifest.json"));
  require(m.is_object() &&
              identity(m.value("BuildHash", Json{})) == identity(build),
          "Build identity differs");
  auto inventory = m.value("Files", Json{});
  require(inventory.is_object() && inventory.size() + 1 == files.size(),
          "Archive inventory differs from manifest");
  for (auto i = inventory.begin(); i != inventory.end(); ++i) {
    safe_member(i.key());
    require(files.contains(i.key()) &&
                sha256(files.at(i.key())) == lower(identity(i.value())),
            "Archive content checksum mismatch: " + i.key());
  }
  require(!inventory.contains("build-manifest.json"),
          "Manifest may not inventory itself");
  auto interface = m.value("EtherCatRuntimeInterfaceVersion", Json{});
  if (!interface.is_null()) {
    require(interface == "workbench-ethercat-v1" &&
                files.contains("ethercat-runtime.json") &&
                files.contains("machine-config.json") &&
                files.contains("io-map.json"),
            "Incomplete EtherCAT runtime bundle");
    require(m.value("EtherCatTopologyFingerprint", Json{}) == topology(files),
            "Manifest topology fingerprint differs");
  } else
    for (auto name :
         {"ethercat-runtime.json", "machine-config.json", "io-map.json"})
      require(!files.contains(name),
              "Hardware dependencies require interface identity");
  if (hardware) {
    std::set<std::string> expected = {"ethercat-runtime.json",
                                      "machine-config.json",
                                      "io-map.json",
                                      "axis-manifest.json",
                                      "hardware-configuration.json",
                                      "build-manifest.json"};
    std::set<std::string> actual;
    for (auto &[n, v] : files)
      actual.insert(n);
    require(actual == expected &&
                m.value("ArtifactKind", Json{}) == "hardware-only" &&
                interface == "workbench-ethercat-v1",
            "Unsupported standalone hardware bundle");
    auto h = parse_json(files.at("hardware-configuration.json"));
    require(h.value("SchemaVersion", Json{}) == 1 &&
                h.value("State", Json{}) == "offline-engineering" &&
                sha256(files.at("hardware-configuration.json")) ==
                    lower(identity(m.value("HardwareHash", Json{}))),
            "Hardware engineering identity differs");
    return;
  }
  require(m.value("ArtifactKind", Json{}) != "hardware-only" &&
              m.value("RuntimeCommit", Json{}) == RuntimeCommit &&
              m.value("CompilerVersion", Json{}) == "STruC++ version 0.7.0",
          "Unapproved Runtime/compiler identity");
  for (auto name : {"generated.cpp", "generated.hpp", "generated_debug.cpp",
                    "defines.h", "debug-map.json", "configuration.st",
                    "strucpp_runtime/include/debug_table.hpp"})
    require(files.contains(name), "Required Runtime artifact missing");
  require(sha256(files.at("debug-map.json")) ==
              lower(identity(m.value("SymbolTableHash", Json{}))),
          "Symbol table identity mismatch");
  auto task = m.value("TaskSchedulingInterfaceVersion", Json{});
  require((task.is_null() && !files.contains("task-scheduling.json")) ||
              (task == "workbench-task-scheduling-v1" &&
               files.contains("task-scheduling.json")),
          "Task scheduling interface differs");
  const bool workers =
      !m.value("AxisInterfaceVersion", Json{}).is_null() ||
      !m.value("CommunicationInterfaceVersion", Json{}).is_null() ||
      !m.value("MotionInterfaceVersion", Json{}).is_null();
  if (workers)
    native_sources(m, files);
  for (auto &[name, body] : files)
    require(!name.ends_with(".py") && !name.ends_with(".pyc"),
            "Python dependencies are not admitted by native coordinator");
}
inline Bundle read_bundle(const fs::path &path, const Json &checksum,
                          const std::string &build, bool hardware = false) {
  auto raw = read_file(path, 32 * 1024 * 1024);
  require(sha256(raw) == lower(identity(checksum)),
          "Package transport checksum mismatch");
  zip_limits(raw);
  std::unique_ptr<archive, decltype(&archive_read_free)> reader(
      archive_read_new(), archive_read_free);
  require(bool(reader), "Archive reader allocation failed");
  archive_read_support_format_zip(reader.get());
  require(archive_read_open_memory(reader.get(), raw.data(), raw.size()) ==
              ARCHIVE_OK,
          "Invalid ZIP archive");
  Bundle result;
  archive_entry *entry = nullptr;
  std::size_t total = 0;
  int code;
  while ((code = archive_read_next_header(reader.get(), &entry)) ==
         ARCHIVE_OK) {
    require(result.files.size() < 1024, "Archive entry capacity exceeded");
    auto n = archive_entry_pathname(entry);
    require(n, "Archive filename missing");
    std::string name(n);
    safe_member(name);
    auto extension = fs::path(name).extension().string();
    const bool license = name == "LICENSE" || name == "COPYING" ||
                         name == "COPYING.RUNTIME" ||
                         name == "RUNTIME-COMPAT-LICENSE" ||
                         fs::path(name).filename() == "LICENSE";
    require(license || extension == ".cpp" || extension == ".hpp" ||
                extension == ".h" || extension == ".st" ||
                extension == ".json" || name == "native/CMakeLists.txt" ||
                name == "runtime_lifecycle.inc" ||
                name == "axis_lifecycle.inc" ||
                name == "communication_lifecycle.inc",
            "Unexpected archive file: " + name);
    require(archive_entry_filetype(entry) == AE_IFREG &&
                archive_entry_symlink(entry) == nullptr &&
                archive_entry_hardlink(entry) == nullptr &&
                !archive_entry_is_encrypted(entry),
            "Archive member must be unencrypted regular file");
    auto size = archive_entry_size(entry);
    require(size >= 0 && size <= 16 * 1024 * 1024 &&
                total + size <= 128 * 1024 * 1024,
            "Archive expanded capacity exceeded");
    total += static_cast<std::size_t>(size);
    std::string body;
    body.reserve(size);
    char buffer[65536];
    for (;;) {
      auto count = archive_read_data(reader.get(), buffer, sizeof buffer);
      require(count >= 0, "Corrupt ZIP content");
      if (!count)
        break;
      require(body.size() + count <= static_cast<std::size_t>(size),
              "ZIP declared size differs");
      body.append(buffer, count);
    }
    require(body.size() == static_cast<std::size_t>(size) &&
                result.files.emplace(name, std::move(body)).second,
            "Duplicate or truncated ZIP entry");
  }
  require(code == ARCHIVE_EOF && !result.files.empty(),
          "Invalid ZIP termination");
  validate_contents(result, build, hardware);
  return result;
}
inline Bundle staged(const fs::path &version) {
  Bundle b;
  auto generated = version / "core/generated";
  auto s = read_json(version / "stage-result.json");
  b.manifest = read_json(generated / "build-manifest.json");
  require(s.value("manifest", Json{}) == b.manifest &&
              identity(s.value("build_hash", Json{})) ==
                  identity(version.filename().string()),
          "Staged manifest identity differs");
  b.files["build-manifest.json"] = read_file(generated / "build-manifest.json");
  auto inventory = b.manifest.value("Files", Json{});
  require(inventory.is_object() && inventory.size() <= 1024,
          "Staged inventory missing");
  for (auto i = inventory.begin(); i != inventory.end(); ++i) {
    safe_member(i.key());
    auto raw = read_file(generated / i.key(), 16 * 1024 * 1024);
    require(sha256(raw) == lower(identity(i.value())),
            "Staged source changed: " + i.key());
    b.files[i.key()] = std::move(raw);
  }
  return b;
}
} // namespace stworkbench::deployment

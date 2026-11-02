// SPDX-License-Identifier: MIT
#pragma once
#include "sha256.hpp"
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <nlohmann/json.hpp>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unistd.h>
#include <vector>
namespace stworkbench {
using Json = nlohmann::json;
class Fd {
  int value_ = -1;

public:
  explicit Fd(int value = -1) noexcept : value_(value) {}
  ~Fd() {
    if (value_ >= 0)
      ::close(value_);
  }
  Fd(const Fd &) = delete;
  Fd &operator=(const Fd &) = delete;
  Fd(Fd &&other) noexcept : value_(other.release()) {}
  Fd &operator=(Fd &&other) noexcept {
    if (this != &other)
      reset(other.release());
    return *this;
  }
  int get() const noexcept { return value_; }
  explicit operator bool() const noexcept { return value_ >= 0; }
  int release() noexcept {
    int result = value_;
    value_ = -1;
    return result;
  }
  void reset(int value = -1) noexcept {
    if (value_ >= 0)
      ::close(value_);
    value_ = value;
  }
};
Json parse_json(std::string_view text, std::size_t max_depth = 64);
std::string read_file(const std::filesystem::path &,
                      std::size_t maximum = 4 * 1024 * 1024,
                      bool private_file = false);
void atomic_write(const std::filesystem::path &, std::string_view,
                  unsigned mode = 0600);
std::int64_t monotonic_ns();
struct ProcessResult {
  int exit_code = -1;
  bool timed_out = false;
  bool truncated = false;
  std::string output;
};
ProcessResult
run_process(const std::vector<std::string> &args,
            const std::filesystem::path &cwd = {},
            std::chrono::milliseconds timeout = std::chrono::seconds(30),
            std::size_t max_output = 4 * 1024 * 1024, bool clean_env = false,
            std::string_view input = {});
} // namespace stworkbench

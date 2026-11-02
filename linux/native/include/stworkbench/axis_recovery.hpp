// SPDX-License-Identifier: MIT
#pragma once
#include <filesystem>
#include <stworkbench/common.hpp>
namespace stworkbench::recovery {
// Implemented by the deployment coordinator holding its exclusive transaction
// lock. Every method is mandatory; there is no permissive default adapter.
class Coordinator {
public:
  virtual ~Coordinator() = default;
  virtual Json active() = 0;
  virtual void verify_active(const Json &,
                             const std::string &expected_build) = 0;
  virtual std::string plc_status() = 0;
  virtual Json transaction() = 0; // null means no pending transaction.
  virtual std::int64_t native_core_pid() = 0;
  // Must verify compiled library/build, installed mapping bytes, v2 native
  // source identity, capabilities, and applied topology before returning
  // machine JSON.
  virtual Json admit_resources(const Json &active) = 0;
  virtual Json snapshot() = 0;
  // These operate ONLY on the unprivileged axis bridge, never PLC/core process.
  virtual void stop_axis_bridge() = 0;
  virtual void resume_axis_bridge() = 0;
  virtual void verify_start_hardware(const Json &active) = 0;
};
void verify_quiescent(Coordinator &, const Json &machine);
Json recover_active(Coordinator &, const std::string &expected_build);
} // namespace stworkbench::recovery

// SPDX-License-Identifier: MIT
#include "axis_diagnostics.hpp"
#include <stworkbench/axis_recovery.hpp>
namespace stworkbench::recovery {
using diagnostics::require;
void verify_quiescent(Coordinator &client, const Json &machine) {
  diagnostics::StationaryProof proof(machine);
  auto deadline =
      monotonic_ns() + std::int64_t(proof.duration_ns()) + 2000000000LL;
  while (monotonic_ns() < deadline) {
    auto sample = client.snapshot();
    auto now = monotonic_ns();
    require(now >= 0, "Invalid stationary clock");
    if (proof.observe(sample, std::uint64_t(now)))
      return;
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
  }
  throw std::runtime_error("Stationary recovery proof timed out");
}
Json recover_active(Coordinator &client, const std::string &expected_build) {
  hardware::identity(expected_build);
  auto active = client.active();
  client.verify_active(active, expected_build);
  require(client.plc_status() == "STOPPED",
          "Axis recovery requires PLC STOPPED");
  auto transaction = client.transaction();
  require(transaction.is_null() ||
              (transaction.is_object() &&
               (transaction.value("state", Json()) == "committed" ||
                transaction.value("state", Json()) == "rolled-back")),
          "Unfinished program transaction must be resolved first");
  auto pid = client.native_core_pid();
  require(pid > 0, "Native core identity unavailable");
  auto machine = client.admit_resources(active);
  verify_quiescent(client, machine);
  require(client.native_core_pid() == pid,
          "Native core identity changed during admission");
  require(client.active() == active,
          "Active program changed during recovery admission");
  client.stop_axis_bridge();
  require(client.native_core_pid() == pid,
          "Native core changed after bridge stop; query actual state");
  verify_quiescent(client, machine);
  client.verify_active(active, expected_build);
  require(client.active() == active, "Active program changed during recovery");
  client.resume_axis_bridge(); // Never retry if the outcome is unknown.
  require(client.native_core_pid() == pid,
          "Native core changed during bridge recovery");
  client.verify_active(active, expected_build);
  client.verify_start_hardware(active);
  require(client.plc_status() == "STOPPED",
          "Recovered bridge but PLC state unconfirmed");
  verify_quiescent(client, machine);
  require(client.native_core_pid() == pid,
          "Native core changed after final recovery proof");
  require(client.active() == active, "Active program changed after recovery");
  return {{"state", "axis-channel-recovered"},
          {"active_build_hash", active.at("build_hash")},
          {"plc_state", "STOPPED"},
          {"motion_restarted", false}};
}
} // namespace stworkbench::recovery

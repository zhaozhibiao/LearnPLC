// SPDX-License-Identifier: MIT
#pragma once
#include "deployment_coordinator.hpp"
namespace stworkbench::deployment {
class RecoveryAdapter final : public recovery::Coordinator {
  deployment::Coordinator &owner;
  fs::path selected;
  std::string interface;

public:
  explicit RecoveryAdapter(deployment::Coordinator &c) : owner(c) {}
  Json active() override { return owner.active(); }
  void verify_active(const Json &a, const std::string &build) override {
    owner.verify_active(a, build);
  }
  std::string plc_status() override {
    Runtime r(owner.config);
    return r.status();
  }
  Json transaction() override {
    return fs::exists(owner.transaction_path)
               ? read_json(owner.transaction_path)
               : Json{};
  }
  int64_t native_core_pid() override {
    return deployment::Coordinator::core_pid();
  }
  Json admit_resources(const Json &a) override {
    auto original = owner.version(a.at("build_hash"));
    compiled_candidate(original);
    verify_applied(original);
    auto b = staged(original);
    selected = owner.worker_version(a);
    auto native = staged(selected);
    require(!native.manifest.value("AxisInterfaceVersion", Json{}).is_null(),
            "Active program has no axis bridge");
    interface = native.manifest.at("AxisInterfaceVersion");
    auto c = channel(owner.config, interface);
    native_receipt(selected, native, c, true);
    verify_environment(c, runtime_environment());
    require(read_file(fs::path(c.endpoint).parent_path() / "axis-manifest.json",
                      65536) == native.files.at("axis-manifest.json"),
            "Installed recovery mapping differs");
    return parse_json(b.files.at("machine-config.json"));
  }
  Json snapshot() override { return deployment::snapshot(); }
  void stop_axis_bridge() override {
    user_service("stop", channel(owner.config, interface).unit);
  }
  void resume_axis_bridge() override {
    user_service("start", channel(owner.config, interface).unit);
    owner.wait_ready(selected);
  }
  void verify_start_hardware(const Json &a) override {
    owner.verify_start_hardware(a);
  }
};
} // namespace stworkbench::deployment

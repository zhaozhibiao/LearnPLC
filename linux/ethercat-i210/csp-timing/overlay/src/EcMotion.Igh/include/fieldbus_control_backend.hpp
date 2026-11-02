#pragma once

#include "control_backend.hpp"
#include "realtime_exchange.hpp"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <map>
#include <string>
#include <vector>

namespace ecmotion {

class FieldbusControlBackend final : public ControlBackend {
public:
    FieldbusControlBackend(RealtimeExchange& exchange, std::vector<std::string> digital_output_names,
        std::vector<std::string> axis_names = {"Axis1"},
        std::vector<std::string> digital_input_names = {},
        std::vector<std::string> axis_position_units = {},
        std::vector<std::string> scalar_names = {}, IoHardwareCatalog catalog = {});

    [[nodiscard]] AxisSnapshot axis_snapshot(const std::string& axis) const override;
    void begin_snapshot_read() const override;
    void end_snapshot_read() const noexcept override;
    [[nodiscard]] std::vector<std::string> axis_names() const override { return axis_names_; }
    [[nodiscard]] std::map<std::string, bool> digital_outputs() const override;
    [[nodiscard]] std::map<std::string, bool> digital_inputs() const override;
    [[nodiscard]] std::vector<ScalarIoSnapshot> scalar_io() const override;
    // Initialization only: called before protocol readers and cyclic threads start.
    void set_ethercat_topology_fingerprint(std::string fingerprint) { ethercat_topology_fingerprint_=std::move(fingerprint); }
    [[nodiscard]] std::string ethercat_topology_fingerprint() const override { return ethercat_topology_fingerprint_; }
    [[nodiscard]] bool supports_motion_migration() const noexcept override { return true; }
    [[nodiscard]] IoHardwareCatalog io_catalog() const override { return catalog_; }
    [[nodiscard]] bool outputs_at_safe_state() const override;
    [[nodiscard]] bool runtime_ready() const noexcept override;
    [[nodiscard]] bool motion_ready() const noexcept override;
    [[nodiscard]] bool io_ready() const noexcept override;
    [[nodiscard]] std::pair<std::uint64_t, std::uint64_t> sample_stamp() const noexcept override;
    [[nodiscard]] std::pair<std::uint64_t, std::uint64_t> applied_command_stamp()
        const noexcept override;
    [[nodiscard]] std::string diagnostics() const override;

    void enable(const std::string& axis) override;
    void disable(const std::string& axis) override;
    void reset(const std::string& axis) override;
    void home(const std::string& axis) override;
    std::uint64_t move_absolute(const std::string& axis, double position, double velocity) override;
    [[nodiscard]] bool supports_axis_move_profile_v2() const noexcept override { return true; }
    [[nodiscard]] bool supports_axis_velocity_profile_v2() const noexcept override { return true; }
    std::uint64_t axis_profile_command(const std::string& axis, RealtimeCommand command) override;
    std::uint64_t move_group(const GroupMotionCommand& command) override;
    [[nodiscard]] bool supports_paths() const noexcept override { return true; }
    [[nodiscard]] PathExecutionSnapshot path_execution() const noexcept override;
    std::uint64_t start_path(const RealtimePath& path, const std::vector<std::string>& axes) override;
    void jog(const std::string& axis, double velocity) override;
    void stop(const std::string& axis) override;
    void write_outputs(const std::map<std::string, bool>& values) override;
    std::uint64_t scan_batch(const std::vector<RealtimeCommand>& commands,
        const std::vector<std::string>& axes, const std::vector<std::string>& outputs,
        std::uint64_t deadline_ns) override;
    std::uint64_t scan_batch_with_io(const std::vector<RealtimeCommand>& commands,
        const std::vector<std::string>& axes, const std::vector<std::string>& outputs,
        const std::vector<IoRawWrite>& writes, std::uint64_t deadline_ns) override;
    void apply_safe_state() noexcept override;
    void tick(double elapsed_seconds) noexcept override;

    void set_bus_ready(bool ready) noexcept { bus_ready_.store(ready, std::memory_order_relaxed); }

private:
    [[nodiscard]] RealtimeSnapshot read_snapshot() const noexcept;
    [[nodiscard]] std::uint16_t validate_axis(const std::string& axis) const;
    static const char* state_name(RealtimeAxisState state) noexcept;
    std::uint64_t submit(RealtimeCommand command);

    RealtimeExchange& exchange_;
    std::vector<std::string> digital_output_names_;
    std::vector<std::string> digital_input_names_;
    std::vector<std::string> axis_names_;
    std::vector<std::string> axis_position_units_;
    std::vector<std::string> scalar_names_;
    IoHardwareCatalog catalog_;
    std::string ethercat_topology_fingerprint_;
    std::map<std::string, std::size_t> output_indices_;
    std::atomic_bool bus_ready_{false};
};

} // namespace ecmotion

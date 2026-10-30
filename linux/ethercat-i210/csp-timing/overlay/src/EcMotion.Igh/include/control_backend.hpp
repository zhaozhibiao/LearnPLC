#pragma once
#include "realtime_exchange.hpp"
#include "io_catalog.hpp"

#include <map>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace ecmotion {

enum class GroupMotionKind { Line, Arc, SpatialLine, SpatialArc };

struct GroupMotionCommand {
    GroupMotionKind kind{GroupMotionKind::Line};
    std::string x_axis;
    std::string y_axis;
    double x_target{};
    double y_target{};
    double velocity{};
    double acceleration{};
    double deceleration{};
    double center_x{};
    double center_y{};
    bool clockwise{};
    std::string z_axis;
    std::string fourth_axis;
    double z_target{};
    double fourth_target{};
    double center_z{};
    double normal_x{};
    double normal_y{};
    double normal_z{1.0};
};

struct AxisSnapshot {

    std::string name;
    std::string state;
    double actual_position{};
    double actual_velocity{};
    bool homed{};
    std::string fault;
    std::uint64_t motion_id{};
    double motion_target{};
    bool motion_busy{};
    bool motion_done{};
    bool motion_aborted{};
    double cycle_target{};
    double cycle_error{};
    double peak_error{};
    double max_excess_ms{};
    double feedforward_velocity{};
    double peak_actual_velocity{};
    bool motion_timing_ready{true};
    bool motion_timing_fault{};

};
struct ScalarIoSnapshot {
    std::string name;
    double engineering_value{};
    std::uint32_t raw_bits{};
    std::uint8_t quality{2};
    std::uint8_t encoding{};
    bool output{};
};
struct PathExecutionSnapshot {
    std::uint64_t id{};
    std::uint16_t completed{}, total{};
    std::uint8_t status{};
};

class ControlBackend {
public:
    virtual ~ControlBackend() = default;
    // A protocol reader may pin one immutable cycle while formatting its reply.
    // Default backends retain the before/after stamp verification fallback.
    virtual void begin_snapshot_read() const {}
    virtual void end_snapshot_read() const noexcept {}

    [[nodiscard]] virtual AxisSnapshot axis_snapshot(const std::string& axis) const = 0;
    [[nodiscard]] virtual std::vector<std::string> axis_names() const { return {"Axis1"}; }
    [[nodiscard]] virtual std::map<std::string, bool> digital_outputs() const = 0;
    [[nodiscard]] virtual std::map<std::string, bool> digital_inputs() const { return {}; }
    [[nodiscard]] virtual std::vector<ScalarIoSnapshot> scalar_io() const { return {}; }
    [[nodiscard]] virtual std::string ethercat_topology_fingerprint() const { return {}; }
    [[nodiscard]] virtual IoHardwareCatalog io_catalog() const { return {}; }
    [[nodiscard]] virtual bool outputs_at_safe_state() const {
        for (const auto& [name, value] : digital_outputs()) { static_cast<void>(name); if (value) return false; }
        return scalar_io().empty();
    }
    [[nodiscard]] virtual bool runtime_ready() const noexcept = 0;
    [[nodiscard]] virtual bool motion_ready() const noexcept { return runtime_ready(); }
    [[nodiscard]] virtual bool io_ready() const noexcept { return runtime_ready(); }
    [[nodiscard]] virtual std::pair<std::uint64_t, std::uint64_t> sample_stamp() const noexcept {
        return {0, 0};
    }
    [[nodiscard]] virtual std::pair<std::uint64_t, std::uint64_t> applied_command_stamp()
        const noexcept { return {0, 0}; }
    [[nodiscard]] virtual std::string diagnostics() const = 0;

    virtual void enable(const std::string& axis) = 0;
    virtual void disable(const std::string& axis) = 0;
    virtual void reset(const std::string& axis) = 0;
    virtual void home(const std::string& axis) = 0;
    virtual std::uint64_t move_absolute(const std::string& axis, double position, double velocity) = 0;
    virtual std::uint64_t move_group(const GroupMotionCommand& command) {
        static_cast<void>(command);
        throw std::logic_error("This backend does not support synchronized group motion.");
    }
    [[nodiscard]] virtual bool supports_paths() const noexcept { return false; }
    [[nodiscard]] virtual PathExecutionSnapshot path_execution() const noexcept { return {}; }
    virtual std::uint64_t start_path(const RealtimePath& path, const std::vector<std::string>& axes) {
        static_cast<void>(path); static_cast<void>(axes);
        throw std::logic_error("This backend does not support autonomous paths.");
    }
    virtual void jog(const std::string& axis, double velocity) = 0;
    virtual void stop(const std::string& axis) = 0;
    virtual void write_outputs(const std::map<std::string, bool>& values) = 0;
    virtual std::uint64_t scan_batch(const std::vector<RealtimeCommand>& commands,
        const std::vector<std::string>& axes, const std::vector<std::string>& outputs,
        std::uint64_t deadline_ns);
    virtual std::uint64_t scan_batch_with_io(const std::vector<RealtimeCommand>& commands,
        const std::vector<std::string>& axes, const std::vector<std::string>& outputs,
        const std::vector<IoRawWrite>& writes, std::uint64_t deadline_ns) {
        if (!writes.empty()) throw std::logic_error("Typed IO batch is not supported by this backend.");
        return scan_batch(commands, axes, outputs, deadline_ns);
    }
    virtual void apply_safe_state() noexcept = 0;
    virtual void tick(double elapsed_seconds) noexcept = 0;
};

} // namespace ecmotion

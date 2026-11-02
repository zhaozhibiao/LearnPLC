#include "fieldbus_control_backend.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <thread>
#include <utility>

namespace ecmotion {
namespace {
constexpr auto command_timeout = std::chrono::milliseconds(200);
thread_local const FieldbusControlBackend* pinned_owner{};
thread_local RealtimeSnapshot pinned_snapshot;
}

void FieldbusControlBackend::begin_snapshot_read() const {
    if (pinned_owner) throw std::logic_error("Nested snapshot reads are not supported.");
    pinned_snapshot = exchange_.snapshot();
    pinned_owner = this;
}
void FieldbusControlBackend::end_snapshot_read() const noexcept {
    if (pinned_owner == this) pinned_owner = nullptr;
}
RealtimeSnapshot FieldbusControlBackend::read_snapshot() const noexcept {
    return pinned_owner == this ? pinned_snapshot : exchange_.snapshot();
}

FieldbusControlBackend::FieldbusControlBackend(
    RealtimeExchange& exchange, std::vector<std::string> digital_output_names,
    std::vector<std::string> axis_names, std::vector<std::string> digital_input_names,
    std::vector<std::string> axis_position_units, std::vector<std::string> scalar_names, IoHardwareCatalog catalog)
    : exchange_(exchange), digital_output_names_(std::move(digital_output_names)),
      digital_input_names_(std::move(digital_input_names)), axis_names_(std::move(axis_names)),
      axis_position_units_(std::move(axis_position_units)), scalar_names_(std::move(scalar_names)),
      catalog_(std::move(catalog)) {
    if (!catalog_.fingerprint.empty()) {
        std::vector<std::string> inputs, outputs, scalars;
        for (const auto& channel : catalog_.channels)
            (channel.type != "BOOL" ? scalars : channel.output ? outputs : inputs).push_back(channel.name);
        if (inputs != digital_input_names_ || outputs != digital_output_names_ || scalars != scalar_names_)
            throw std::invalid_argument("IO catalog order does not match the realtime channel tables.");
    }
    if (scalar_names_.size() > RealtimeSnapshot::maximum_scalar_channels)
        throw std::invalid_argument("At most 512 scalar IO names are supported.");
    for (std::size_t i = 0; i < scalar_names_.size(); ++i)
        if (scalar_names_[i].empty() || std::find(scalar_names_.begin(), scalar_names_.begin() + i,
            scalar_names_[i]) != scalar_names_.begin() + i)
            throw std::invalid_argument("Duplicate or empty scalar IO name.");
    if (axis_names_.empty() || axis_names_.size() > RealtimeSnapshot::maximum_axes)
        throw std::invalid_argument("Fieldbus backend requires 1..256 axes.");
    for (std::size_t i = 0; i < axis_names_.size(); ++i) {
        if (axis_names_[i].empty() || std::find(axis_names_.begin(), axis_names_.begin() + i, axis_names_[i])
            != axis_names_.begin() + i) throw std::invalid_argument("Duplicate or empty axis name.");
    }
    if (axis_position_units_.empty()) axis_position_units_.resize(axis_names_.size());
    if (axis_position_units_.size() != axis_names_.size())
        throw std::invalid_argument("Axis position-unit count does not match axis count.");
    if (digital_output_names_.size() > 64U) {
        throw std::invalid_argument("Fieldbus backend supports at most 64 digital outputs.");
    }
    if (digital_input_names_.size() > 64U) {
        throw std::invalid_argument("Fieldbus backend supports at most 64 digital inputs.");
    }
    for (std::size_t index = 0; index < digital_output_names_.size(); ++index) {
        if (!output_indices_.emplace(digital_output_names_[index], index).second) {
            throw std::invalid_argument("Duplicate digital output tag: " + digital_output_names_[index]);
        }
    }
}

AxisSnapshot FieldbusControlBackend::axis_snapshot(const std::string& axis) const {
    const auto index = validate_axis(axis);
    const auto snapshot = read_snapshot();
    const auto& value = snapshot.axes[index];
    std::string fault;
    if (value.homing_failed) {
        fault = "Homing failed, timed out or lost drive readiness; reset and re-reference the axis.";
    } else if (value.error_code != 0U) {
        std::ostringstream message;
        message << "CiA402 error 0x" << std::hex << std::uppercase
                << std::setw(4) << std::setfill('0') << value.error_code;
        fault = message.str();
    } else if (value.software_limit_violation) {
        fault = "Actual position is outside the configured software limits.";
    } else if (value.demand_delivery_fault) {
        fault = "Configured command-to-demand delivery diagnostic enforcement failed; reset after delivery recovers.";
    } else if (value.following_error_fault) {
        fault = "Following error exceeded the configured limit and duration.";
    } else if (value.overspeed_fault) {
        fault = "Actual velocity exceeded the configured motion safety limit.";
    } else if (value.motion_timing_fault) {
        fault="Motion delivery timing lost its validated window; reset after timing recovers.";
    } else if (!snapshot.bus_ready) {
        fault = "EtherCAT bus is not ready; motion outputs are forced safe.";
    } else if (value.commissioning_required) {
        fault = "Axis mechanics require commissioning; motion commands are blocked.";
    }
    return {axis, state_name(value.state), value.actual_position,
        value.actual_velocity, value.homed, std::move(fault), value.motion_id,
        value.motion_target, value.motion_busy, value.motion_done, value.motion_aborted, value.cycle_target, value.cycle_error, value.peak_error, value.max_excess_ms, value.feedforward_velocity, value.peak_actual_velocity,value.motion_timing_ready,value.motion_timing_fault,
        value.demand_position_error,value.drive_following_error,value.demand_delivery_error,
        value.demand_feedback_valid,value.demand_delivery_mismatch,value.demand_delivery_fault,true,value.statusword};
}

std::vector<ScalarIoSnapshot> FieldbusControlBackend::scalar_io() const {
    const auto snapshot = read_snapshot();
    if (snapshot.scalar_count != scalar_names_.size())
        throw std::logic_error("Scalar IO snapshot does not match the configured catalog.");
    std::vector<ScalarIoSnapshot> result;
    result.reserve(snapshot.scalar_count);
    for (std::size_t i = 0; i < snapshot.scalar_count; ++i) {
        const auto& value = snapshot.scalars[i];
        result.push_back({scalar_names_[i], value.engineering_value, value.raw_bits,
            value.quality, value.encoding, value.output});
    }
    return result;
}

bool FieldbusControlBackend::outputs_at_safe_state() const {
    const auto snapshot = read_snapshot();
    if (catalog_.fingerprint.empty()) return snapshot.digital_outputs == 0 && snapshot.scalar_count == 0;
    for (const auto& channel : catalog_.channels) if (channel.output) {
        if (channel.type == "BOOL") {
            const auto index = output_indices_.at(channel.name);
            if (((snapshot.digital_outputs >> index) & 1U) != channel.safe_bits) return false;
        } else {
            const auto index = std::find(scalar_names_.begin(), scalar_names_.end(), channel.name) - scalar_names_.begin();
            if (index >= snapshot.scalar_count || snapshot.scalars[index].raw_bits != channel.safe_bits) return false;
        }
    }
    return true;
}

std::pair<std::uint64_t, std::uint64_t> FieldbusControlBackend::sample_stamp() const noexcept {
    const auto value = read_snapshot();
    return {value.cycles, value.sample_monotonic_ns};
}

std::pair<std::uint64_t, std::uint64_t> FieldbusControlBackend::applied_command_stamp()
    const noexcept {
    const auto value = read_snapshot();
    return {value.last_applied_command_sequence, value.last_applied_command_cycle};
}

std::map<std::string, bool> FieldbusControlBackend::digital_inputs() const {
    const auto bits = read_snapshot().digital_inputs;
    std::map<std::string, bool> result;
    for (std::size_t index = 0; index < digital_input_names_.size(); ++index) {
        result.emplace(digital_input_names_[index], (bits & (std::uint64_t{1} << index)) != 0U);
    }
    return result;
}

std::map<std::string, bool> FieldbusControlBackend::digital_outputs() const {
    const auto bits = read_snapshot().digital_outputs;
    std::map<std::string, bool> result;
    for (std::size_t index = 0; index < digital_output_names_.size(); ++index) {
        result.emplace(digital_output_names_[index], (bits & (std::uint64_t{1} << index)) != 0U);
    }
    return result;
}

bool FieldbusControlBackend::runtime_ready() const noexcept {
    // Axis faults belong to the axis; they must not disconnect engineering,
    // monitoring or configuration identity checks from a healthy fieldbus.
    const auto snapshot = read_snapshot();
    return io_ready() && snapshot.axis_count == axis_names_.size()
        && snapshot.cycles > 0 && snapshot.sample_monotonic_ns > 0;
}

bool FieldbusControlBackend::motion_ready() const noexcept {
    const auto snapshot = read_snapshot();
    if (!runtime_ready()) return false;
    for (std::size_t i = 0; i < snapshot.axis_count; ++i) {
        const auto& axis = snapshot.axes[i];
        if (!axis.motion_timing_ready || axis.motion_timing_fault || axis.commissioning_required || axis.error_code != 0U
            || axis.state == RealtimeAxisState::ErrorStop
            || axis.software_limit_violation
            || axis.following_error_fault || axis.overspeed_fault) return false;
    }
    return true;
}

bool FieldbusControlBackend::io_ready() const noexcept {
    return bus_ready_.load(std::memory_order_relaxed) && read_snapshot().bus_ready;
}

std::string FieldbusControlBackend::diagnostics() const {
    const auto snapshot = read_snapshot();
    std::ostringstream output;
    output << "backend=igh; realtime=true; bus_ready="
           << (bus_ready_.load(std::memory_order_relaxed) ? 1 : 0)
           << "; cycles=" << snapshot.cycles
           << "; rejected_commands=" << snapshot.rejected_commands
           << "; statusword=0x" << std::hex << std::uppercase << snapshot.statusword
           << "; mode=" << std::dec << static_cast<int>(snapshot.mode_display)
           << "; error=0x" << std::hex << std::uppercase << snapshot.error_code
           << "; commissioning_required=" << (snapshot.commissioning_required ? 1 : 0);
    output << "; software_limit_violation=" << (snapshot.software_limit_violation ? 1 : 0);
    output << "; following_error_fault=" << (snapshot.following_error_fault ? 1 : 0)
           << "; overspeed_fault=" << (snapshot.overspeed_fault ? 1 : 0)
           << "; realtime_bus_ready=" << (snapshot.bus_ready ? 1 : 0);
    return output.str();
}

void FieldbusControlBackend::enable(const std::string& axis) {
    submit({.kind = RealtimeCommandKind::Enable, .axis_index = validate_axis(axis)});
}

void FieldbusControlBackend::disable(const std::string& axis) {
    submit({.kind = RealtimeCommandKind::Disable, .axis_index = validate_axis(axis)});
}

void FieldbusControlBackend::reset(const std::string& axis) {
    submit({.kind = RealtimeCommandKind::Reset, .axis_index = validate_axis(axis)});
}

void FieldbusControlBackend::home(const std::string& axis) {
    submit({.kind = RealtimeCommandKind::Home, .axis_index = validate_axis(axis)});
}

std::uint64_t FieldbusControlBackend::move_absolute(
    const std::string& axis, double position, double velocity) {
    return submit({.kind = RealtimeCommandKind::MoveAbsolute, .axis_index = validate_axis(axis),
        .position = position, .velocity = velocity});
}

std::uint64_t FieldbusControlBackend::axis_profile_command(const std::string& axis, RealtimeCommand command) {
    if (command.motion_profile_version != 2 || (command.kind != RealtimeCommandKind::MoveAbsolute
        && command.kind != RealtimeCommandKind::Jog && command.kind != RealtimeCommandKind::Stop))
        throw std::invalid_argument("Unsupported axis profile command.");
    command.axis_index = validate_axis(axis);
    return submit(command);
}

std::uint64_t FieldbusControlBackend::move_group(const GroupMotionCommand& command) {
    const auto x = validate_axis(command.x_axis);
    const auto y = validate_axis(command.y_axis);
    const auto spatial = command.kind == GroupMotionKind::SpatialLine
        || command.kind == GroupMotionKind::SpatialArc;
    const auto has_fourth = spatial && !command.fourth_axis.empty();
    if (spatial && command.z_axis.empty())
        throw std::invalid_argument("Spatial group requires a Z axis.");
    if (!spatial && (!command.z_axis.empty() || !command.fourth_axis.empty()))
        throw std::invalid_argument("XY group cannot include Z or fourth axis.");
    const auto z = spatial ? validate_axis(command.z_axis) : std::uint16_t{};
    const auto fourth = has_fourth ? validate_axis(command.fourth_axis) : std::uint16_t{};
    if (x == y || (spatial && (x == z || y == z))
        || (has_fourth && (fourth == x || fourth == y || fourth == z)))
        throw std::invalid_argument("Group motion requires distinct axes.");
    if (axis_position_units_[x] != axis_position_units_[y])
        throw std::invalid_argument("Group axes require the same engineering position unit.");
    if (spatial && axis_position_units_[x] != axis_position_units_[z])
        throw std::invalid_argument("Spatial XYZ axes require the same engineering unit.");
    if (!std::isfinite(command.x_target) || !std::isfinite(command.y_target)
        || !std::isfinite(command.z_target) || !std::isfinite(command.fourth_target)
        || !std::isfinite(command.center_x) || !std::isfinite(command.center_y)
        || !std::isfinite(command.center_z) || !std::isfinite(command.normal_x)
        || !std::isfinite(command.normal_y) || !std::isfinite(command.normal_z)
        || !std::isfinite(command.velocity) || !std::isfinite(command.acceleration)
        || !std::isfinite(command.deceleration) || command.velocity <= 0.0
        || command.acceleration <= 0.0 || command.deceleration <= 0.0)
        throw std::invalid_argument("Group path parameters must be finite and positive where required.");
    return submit({.kind = command.kind == GroupMotionKind::Arc
            ? RealtimeCommandKind::GroupArc
            : command.kind == GroupMotionKind::SpatialLine ? RealtimeCommandKind::GroupSpatialLine
            : command.kind == GroupMotionKind::SpatialArc ? RealtimeCommandKind::GroupSpatialArc
            : RealtimeCommandKind::GroupLine,
        .axis_index = x, .secondary_axis_index = y,
        .third_axis_index = z, .fourth_axis_index = fourth,
        .group_axis_count = static_cast<std::uint8_t>(spatial ? (has_fourth ? 4 : 3) : 2),
        .position = command.x_target, .secondary_position = command.y_target,
        .third_position = command.z_target, .fourth_position = command.fourth_target,
        .velocity = command.velocity, .acceleration = command.acceleration,
        .deceleration = command.deceleration, .center_x = command.center_x,
        .center_y = command.center_y, .center_z = command.center_z,
        .normal_x = command.normal_x, .normal_y = command.normal_y,
        .normal_z = command.normal_z, .clockwise = command.clockwise});
}

void FieldbusControlBackend::jog(const std::string& axis, double velocity) {
    submit({.kind = RealtimeCommandKind::Jog, .axis_index = validate_axis(axis), .velocity = velocity});
}

std::uint64_t FieldbusControlBackend::start_path(const RealtimePath& source, const std::vector<std::string>& axes) {
    if (axes.size()!=source.axis_count || source.count<2 || source.count>RealtimePath::maximum_points)
        throw std::invalid_argument("Invalid path dimensions.");
    auto path=source;
    for (std::size_t i=0;i<axes.size();++i) {
        path.axes[i]=validate_axis(axes[i]);
        if (axis_position_units_[path.axes[i]]!="mm") throw std::invalid_argument("Path axes must use mm.");
    }
    RealtimeCommand command{.sequence=exchange_.next_command_sequence(), .kind=RealtimeCommandKind::StartPath};
    if (!exchange_.prepare_path(path,command.sequence)) throw std::logic_error("A path is already pending or executing.");
    if (!exchange_.try_enqueue(command)) { exchange_.finish_path(); throw std::runtime_error("Path command queue full."); }
    const auto deadline=std::chrono::steady_clock::now()+command_timeout;
    while (exchange_.completed_sequence()<command.sequence) {
        if (std::chrono::steady_clock::now()>=deadline) {
            exchange_.cancel_through(command.sequence); static_cast<void>(exchange_.request_safe_state());
            throw std::runtime_error("Path application timed out.");
        }
        std::this_thread::sleep_for(std::chrono::microseconds(100));
    }
    if (exchange_.rejected_sequence()==command.sequence) throw std::logic_error("Path rejected by realtime safety checks.");
    while (exchange_.snapshot().last_applied_command_sequence<command.sequence) {
        if (std::chrono::steady_clock::now()>=deadline) {
            static_cast<void>(exchange_.request_safe_state()); throw std::runtime_error("Path feedback publication timed out.");
        }
        std::this_thread::sleep_for(std::chrono::microseconds(100));
    }
    return command.sequence;
}
PathExecutionSnapshot FieldbusControlBackend::path_execution() const noexcept {
    const auto s=read_snapshot();
    return {s.path_execution_id,s.path_completed_points,s.path_total_points,s.path_status};
}

void FieldbusControlBackend::stop(const std::string& axis) {
    submit({.kind = RealtimeCommandKind::Stop, .axis_index = validate_axis(axis)});
}

void FieldbusControlBackend::write_outputs(const std::map<std::string, bool>& values) {
    RealtimeCommand command{.kind = RealtimeCommandKind::WriteOutputs};
    for (const auto& [name, value] : values) {
        const auto found = output_indices_.find(name);
        if (found == output_indices_.end()) throw std::invalid_argument("Unknown digital output '" + name + "'.");
        const auto bit = std::uint64_t{1} << found->second;
        command.output_mask |= bit;
        if (value) command.output_values |= bit;
    }
    submit(command);
}

void FieldbusControlBackend::apply_safe_state() noexcept {
    exchange_.cancel_through(exchange_.next_command_sequence());
    const auto expected_epoch = exchange_.request_safe_state();
    const auto deadline = std::chrono::steady_clock::now() + command_timeout;
    while (exchange_.safe_state_epoch() < expected_epoch
           && std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::microseconds(100));
    }
}

std::uint64_t FieldbusControlBackend::scan_batch(const std::vector<RealtimeCommand>& commands,
    const std::vector<std::string>& axes, const std::vector<std::string>& outputs,
    std::uint64_t deadline_ns) {
    return scan_batch_with_io(commands, axes, outputs, {}, deadline_ns);
}

std::uint64_t FieldbusControlBackend::scan_batch_with_io(const std::vector<RealtimeCommand>& commands,
    const std::vector<std::string>& axes, const std::vector<std::string>& outputs,
    const std::vector<IoRawWrite>& writes, std::uint64_t deadline_ns) {
    if ((commands.empty() && writes.empty()) || commands.size() > 16 || writes.size() > 512)
        throw std::invalid_argument("Invalid scan batch size.");
    RealtimeBatch batch;
    batch.count = static_cast<std::uint16_t>(commands.size());
    batch.deadline_ns = deadline_ns;
    const auto sequence = exchange_.next_command_sequence();
    batch.sequence = sequence;
    std::vector<std::string> seen;
    for (const auto& write : writes) {
        if (std::find(seen.begin(), seen.end(), write.name) != seen.end())
            throw std::invalid_argument("Duplicate typed IO output.");
        seen.push_back(write.name);
        const auto found = std::find_if(catalog_.channels.begin(), catalog_.channels.end(),
            [&](const auto& channel) { return channel.name == write.name; });
        if (found == catalog_.channels.end() || !found->output || found->commissioning_required)
            throw std::invalid_argument("Typed output is unknown, read-only or uncommissioned.");
        if (found->type == "BOOL") {
            if (write.raw_bits > 1) throw std::invalid_argument("Boolean output requires 0/1.");
            const auto bit = std::uint64_t{1} << output_indices_.at(write.name);
            batch.io_output_mask |= bit;
            if (write.raw_bits) batch.io_output_values |= bit;
        } else {
            if (found->type == "INT" && write.raw_bits > 0xFFFFU)
                throw std::invalid_argument("INT output raw width exceeds 16 bits.");
            const auto index = std::find(scalar_names_.begin(), scalar_names_.end(), write.name);
            if (index == scalar_names_.end()) throw std::invalid_argument("Missing scalar catalog index.");
            batch.scalar_writes[batch.scalar_write_count++] = {
                static_cast<std::uint16_t>(index - scalar_names_.begin()), write.raw_bits};
        }
    }
    auto axis = [&](std::uint16_t i) {
        if (i >= axes.size()) throw std::invalid_argument("Unknown scan axis.");
        return validate_axis(axes[i]);
    };
    for (std::size_t i=0;i<commands.size();++i) {
        auto c = commands[i]; c.sequence = sequence;
        if (c.kind == RealtimeCommandKind::WriteScalarOutput) {
            // scalar_index belongs to the catalog returned by scalar_io(), never to the axis table.
            if (c.scalar_index >= scalar_names_.size())
                throw std::invalid_argument("Unknown scalar IO catalog index.");
            const auto snapshot = exchange_.snapshot();
            if (snapshot.scalar_count != scalar_names_.size() || !snapshot.scalars[c.scalar_index].output
                || snapshot.scalars[c.scalar_index].quality != 0)
                throw std::invalid_argument("Scalar output is unavailable or not commissioned.");
        } else if (c.kind == RealtimeCommandKind::WriteOutputs) {
            if (outputs.size()<64 && (c.output_mask >> outputs.size()) != 0)
                throw std::invalid_argument("Unknown scan output.");
            const auto mask=c.output_mask, values=c.output_values;
            c.output_mask=c.output_values=0;
            for(std::size_t j=0;j<outputs.size() && j<64;++j) if(mask & (std::uint64_t{1}<<j)) {
                const auto found=output_indices_.find(outputs[j]);
                if(found==output_indices_.end()) throw std::invalid_argument("Unknown scan output.");
                const auto bit=std::uint64_t{1}<<found->second;
                c.output_mask|=bit; if(values & (std::uint64_t{1}<<j)) c.output_values|=bit;
            }
        } else {
            c.axis_index=axis(c.axis_index);
            if(c.kind==RealtimeCommandKind::GroupLine || c.kind==RealtimeCommandKind::GroupArc
                || c.kind==RealtimeCommandKind::GroupSpatialLine || c.kind==RealtimeCommandKind::GroupSpatialArc) {
                c.secondary_axis_index=axis(c.secondary_axis_index);
                if(c.group_axis_count>2) c.third_axis_index=axis(c.third_axis_index);
                if(c.group_axis_count>3) c.fourth_axis_index=axis(c.fourth_axis_index);
            }
        }
        batch.commands[i]=c;
    }
    if(!exchange_.try_enqueue_batch(batch)) throw std::runtime_error("Scan batch queue full.");
    const auto deadline=std::chrono::steady_clock::now()+command_timeout;
    while(exchange_.completed_sequence()<sequence) {
        if(std::chrono::steady_clock::now()>=deadline) {
            exchange_.cancel_through(sequence); static_cast<void>(exchange_.request_safe_state());
            throw std::runtime_error("Scan batch application timed out.");
        }
        std::this_thread::sleep_for(std::chrono::microseconds(100));
    }
    if(exchange_.rejected_sequence()==sequence) throw std::logic_error("Scan batch rejected as a whole.");
    // complete() is published inside the RT command phase; the corresponding
    // axis motion ID and applied stamp are published only at the end of that cycle.
    // Never return a successful receipt backed by the preceding cycle's snapshot.
    while (exchange_.snapshot().last_applied_command_sequence < sequence) {
        if (std::chrono::steady_clock::now() >= deadline) {
            exchange_.cancel_through(sequence); static_cast<void>(exchange_.request_safe_state());
            throw std::runtime_error("Scan batch feedback publication timed out.");
        }
        std::this_thread::sleep_for(std::chrono::microseconds(100));
    }
    return sequence;
}

void FieldbusControlBackend::tick(double elapsed_seconds) noexcept {
    static_cast<void>(elapsed_seconds);
}

std::uint16_t FieldbusControlBackend::validate_axis(const std::string& axis) const {
    const auto found = std::find(axis_names_.begin(), axis_names_.end(), axis);
    if (found == axis_names_.end()) throw std::invalid_argument("Unknown axis '" + axis + "'.");
    return static_cast<std::uint16_t>(std::distance(axis_names_.begin(), found));
}

const char* FieldbusControlBackend::state_name(RealtimeAxisState state) noexcept {
    switch (state) {
        case RealtimeAxisState::Standstill: return "Standstill";
        case RealtimeAxisState::Homing: return "Homing";
        case RealtimeAxisState::DiscreteMotion: return "DiscreteMotion";
        case RealtimeAxisState::ContinuousMotion: return "ContinuousMotion";
        case RealtimeAxisState::Stopping: return "Stopping";
        case RealtimeAxisState::ErrorStop: return "ErrorStop";
        case RealtimeAxisState::Disabled: return "Disabled";
    }
    return "Disabled";
}

std::uint64_t FieldbusControlBackend::submit(RealtimeCommand command) {
    command.sequence = exchange_.next_command_sequence();
    if (!exchange_.try_enqueue(command)) throw std::runtime_error("Realtime command queue is full.");
    const auto deadline = std::chrono::steady_clock::now() + command_timeout;
    while (exchange_.completed_sequence() < command.sequence) {
        if (std::chrono::steady_clock::now() >= deadline) {
            exchange_.cancel_through(command.sequence);
            static_cast<void>(exchange_.request_safe_state());
            throw std::runtime_error("Realtime command acknowledgement timed out.");
        }
        std::this_thread::sleep_for(std::chrono::microseconds(100));
    }
    if (exchange_.rejected_sequence() == command.sequence) {
        throw std::logic_error("Realtime controller rejected the command for its current state.");
    }
    while (exchange_.snapshot().last_applied_command_sequence < command.sequence) {
        if (std::chrono::steady_clock::now() >= deadline) {
            exchange_.cancel_through(command.sequence);
            static_cast<void>(exchange_.request_safe_state());
            throw std::runtime_error("Realtime command feedback publication timed out.");
        }
        std::this_thread::sleep_for(std::chrono::microseconds(100));
    }
    return command.sequence;
}

} // namespace ecmotion

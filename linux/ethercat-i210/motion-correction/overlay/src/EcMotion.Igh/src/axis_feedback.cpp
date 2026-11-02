#include "axis_feedback.hpp"

#include <nlohmann/json.hpp>

#include <cmath>
#include <fstream>
#include <limits>
#include <set>
#include <stdexcept>
#include <utility>

namespace ecmotion {
namespace {
using json = nlohmann::json;

void require_input(const PdoLayout& layout, PdoHandle handle, std::uint8_t bits, const std::string& name) {
    const auto& entry = layout.entry(handle);
    if (entry.direction != PdoDirection::ControllerInput || entry.bit_length != bits) {
        throw std::invalid_argument(name + ": PDO direction or width is invalid: " + entry.key);
    }
}

void require_output(const PdoLayout& layout, PdoHandle handle, std::uint8_t bits, const std::string& name) {
    const auto& entry = layout.entry(handle);
    if (entry.direction != PdoDirection::ControllerOutput || entry.bit_length != bits) {
        throw std::invalid_argument(name + ": PDO direction or width is invalid: " + entry.key);
    }
}

double positive_finite(const json& value, const char* name) {
    const auto result = value.at(name).get<double>();
    if (!std::isfinite(result) || result <= 0.0) {
        throw std::invalid_argument(std::string(name) + " must be positive and finite.");
    }
    return result;
}

double finite_value(const json& value, const char* name) {
    const auto result = value.at(name).get<double>();
    if (!std::isfinite(result)) throw std::invalid_argument(std::string(name) + " must be finite.");
    return result;
}
} // namespace

AxisFeedbackConfiguration AxisFeedbackConfiguration::load(
    const std::string& path, const std::string& axis_name) {
    std::ifstream stream(path);
    if (!stream) throw std::invalid_argument("Cannot open machine configuration: " + path);
    json root;
    stream >> root;
    if (root.at("schemaVersion").get<int>() != 1) {
        throw std::invalid_argument("Unsupported machine configuration schema.");
    }
    for (const auto& axis : root.at("axes")) {
        if (axis.at("name").get<std::string>() != axis_name) continue;
        const auto& mechanics = axis.at("mechanics");
        const auto encoder_counts = positive_finite(mechanics, "encoderCountsPerMotorRevolution");
        const auto motor_turns = positive_finite(mechanics, "motorTurnsPerOutputTurn");
        const auto engineering_units = positive_finite(mechanics, "engineeringUnitsPerOutputTurn");
        const auto counts_per_unit = encoder_counts * motor_turns / engineering_units;
        if (!std::isfinite(counts_per_unit) || counts_per_unit <= 0.0) {
            throw std::invalid_argument("Axis counts per engineering unit are invalid.");
        }
        const auto direction = mechanics.at("direction").get<int>();
        if (direction != -1 && direction != 1) throw std::invalid_argument("Axis direction must be -1 or 1.");
        const auto mechanics_commissioning_required = mechanics.at("commissioningRequired").get<bool>();
        const auto motor_model = mechanics.at("motorModel").get<std::string>();
        if (!mechanics_commissioning_required
            && (motor_model.empty() || motor_model == "CONFIGURE_BEFORE_ENABLE")) {
            throw std::invalid_argument("Axis motor model must be commissioned before clearing the interlock.");
        }
        const auto& entries = axis.at("pdoEntries");
        const auto profile = axis.at("profile").get<std::string>();
        if (profile != "CiA402") {
            throw std::invalid_argument("Unsupported axis profile '" + profile + "'.");
        }
        const auto operating_mode = axis.value("modeOfOperation", 8);
        if (operating_mode != 8) {
            throw std::invalid_argument("The current CiA 402 cyclic trajectory requires CSP mode 8.");
        }
        const MotionSetpointConfiguration motion{
            root.at("cycleTimeMicroseconds").get<double>() / 1'000'000.0,
            finite_value(axis, "softLimitNegative"),
            finite_value(axis, "softLimitPositive"),
            positive_finite(axis, "maximumVelocity"),
            positive_finite(axis, "maximumAcceleration"),
            positive_finite(axis, "maximumDeceleration")};
        const auto& safety = axis.at("motionSafety");
        const auto safety_commissioning_required = safety.at("commissioningRequired").get<bool>();
        const MotionSafetyConfiguration motion_safety{
            motion.cycle_seconds,
            motion.soft_limit_negative,
            motion.soft_limit_positive,
            positive_finite(safety, "maximumActualVelocity"),
            positive_finite(safety, "maximumFollowingError"),
            positive_finite(safety, "followingErrorTimeoutMilliseconds") / 1000.0};
        const auto completion = axis.value("motionCompletion", json::object());
        const auto position_tolerance = completion.value("positionTolerance", 0.02);
        const auto velocity_tolerance = completion.value("velocityTolerance", 0.1);
        const auto dwell_ms = completion.value("dwellMilliseconds", 20.0);
        if (!std::isfinite(position_tolerance) || position_tolerance <= 0.0
            || !std::isfinite(velocity_tolerance) || velocity_tolerance <= 0.0
            || !std::isfinite(dwell_ms) || dwell_ms <= 0.0) {
            throw std::invalid_argument("Motion completion tolerances and dwell must be positive and finite.");
        }
        AxisFeedbackConfiguration::HomingConfiguration homing;
        if (axis.contains("homing")) {
            const auto& home = axis.at("homing");
            if (!home.is_object() || !home.value("required", true))
                throw std::invalid_argument("Physical axes currently require an enabled homing interlock.");
            const auto method = home.value("method", 0);
            homing.allow_current_position_zero = home.value("allowCurrentPositionZero", false);
            if (method < -128 || method > 127 || method == 37
                || (method == 35 && !homing.allow_current_position_zero))
                throw std::invalid_argument("Homing method is invalid or current-position method 35 lacks explicit permission.");
            homing.method = static_cast<std::int8_t>(method);
            if (method != 0) {
                homing.search_speed_raw = home.at("searchSpeedRaw").get<std::uint32_t>();
                homing.latch_speed_raw = home.at("latchSpeedRaw").get<std::uint32_t>();
                homing.acceleration_raw = home.at("accelerationRaw").get<std::uint32_t>();
                homing.offset_raw = home.at("offsetRaw").get<std::int32_t>();
                homing.timeout_seconds = home.at("timeoutMilliseconds").get<double>() / 1000.0;
                if (homing.search_speed_raw == 0 || homing.latch_speed_raw == 0
                    || homing.acceleration_raw == 0 || !std::isfinite(homing.timeout_seconds)
                    || homing.timeout_seconds < 0.1 || homing.timeout_seconds > 600.0)
                    throw std::invalid_argument("Homing raw parameters or timeout are invalid.");
            }
        }
        const auto velocity_setpoint_key = entries.contains("velocityOffset")
            ? entries.at("velocityOffset").get<std::string>()
            : entries.at("targetVelocity").get<std::string>();
        return {
            axis_name,
            axis.at("slavePosition").get<int>(),
            profile,
            axis.at("positionUnit").get<std::string>(),
            entries.at("controlword").get<std::string>(),
            entries.at("targetPosition").get<std::string>(),
            velocity_setpoint_key,
            entries.at("modeOfOperation").get<std::string>(),
            entries.at("statusword").get<std::string>(),
            entries.at("positionActual").get<std::string>(),
            entries.at("velocityActual").get<std::string>(),
            entries.at("modeDisplay").get<std::string>(),
            entries.at("errorCode").get<std::string>(),
            counts_per_unit,
            direction,
            mechanics_commissioning_required || safety_commissioning_required,
            static_cast<std::int8_t>(operating_mode),
            motion,
            motion_safety,
            homing,
            position_tolerance,
            velocity_tolerance,
            dwell_ms / 1000.0,
            completion.value("positionStabilityCompletion", false),
            axis.value("velocityFeedforwardGain", 1.0),
            axis.contains("velocityFeedbackRawPerUnitPerSecond") ? positive_finite(axis,"velocityFeedbackRawPerUnitPerSecond") : 0.0,
            axis.contains("velocityOffsetRawPerUnitPerSecond") ? positive_finite(axis,"velocityOffsetRawPerUnitPerSecond") : 0.0};
    }
    throw std::invalid_argument("Machine configuration does not contain axis '" + axis_name + "'.");
}

std::vector<AxisFeedbackConfiguration> AxisFeedbackConfiguration::load_all(const std::string& path) {
    std::ifstream stream(path);
    if (!stream) throw std::invalid_argument("Cannot open machine configuration: " + path);
    json root;
    stream >> root;
    if(root.at("schemaVersion").get<int>()!=1) throw std::invalid_argument("Unsupported machine configuration schema.");
    const auto& axes = root.at("axes");
    if (!axes.is_array() || axes.size() > RealtimeSnapshot::maximum_axes)
        throw std::invalid_argument("Machine configuration permits 0..256 axes.");
    std::set<std::string> names;
    std::set<int> slave_positions;
    std::vector<AxisFeedbackConfiguration> result;
    result.reserve(axes.size());
    for (const auto& axis : axes) {
        const auto name = axis.at("name").get<std::string>();
        const auto position = axis.at("slavePosition").get<int>();
        if (name.empty() || !names.insert(name).second || position < 0
            || !slave_positions.insert(position).second)
            throw std::invalid_argument("Axes require unique names and slave positions.");
        result.push_back(load(path, name));
    }
    return result;
}

Cia402AxisAdapter::Cia402AxisAdapter(
    const PdoLayout& layout, AxisFeedbackConfiguration configuration)
    : configuration_(std::move(configuration)),
      controlword_(layout.resolve(configuration_.controlword_key)),
      target_position_(layout.resolve(configuration_.target_position_key)),
      velocity_offset_(layout.resolve(configuration_.velocity_offset_key)),
      mode_of_operation_(layout.resolve(configuration_.mode_of_operation_key)),
      statusword_(layout.resolve(configuration_.statusword_key)),
      position_actual_(layout.resolve(configuration_.position_actual_key)),
      velocity_actual_(layout.resolve(configuration_.velocity_actual_key)),
      mode_display_(layout.resolve(configuration_.mode_display_key)),
      error_code_(layout.resolve(configuration_.error_code_key)),
      setpoint_(configuration_.motion),
      safety_(configuration_.safety) {
    if(configuration_.velocity_feedback_raw_per_unit_per_second==0)
        configuration_.velocity_feedback_raw_per_unit_per_second=configuration_.counts_per_engineering_unit;
    if(configuration_.velocity_offset_raw_per_unit_per_second==0)
        configuration_.velocity_offset_raw_per_unit_per_second=configuration_.counts_per_engineering_unit;
    if(!std::isfinite(configuration_.velocity_feedback_raw_per_unit_per_second) || configuration_.velocity_feedback_raw_per_unit_per_second<=0
       || !std::isfinite(configuration_.velocity_offset_raw_per_unit_per_second) || configuration_.velocity_offset_raw_per_unit_per_second<=0)
        throw std::invalid_argument("Velocity feedback and offset scales must be positive and finite.");
    if (!std::isfinite(configuration_.velocity_feedforward_gain) || configuration_.velocity_feedforward_gain < 0 || configuration_.velocity_feedforward_gain > 1)
        throw std::invalid_argument("Velocity feedforward gain must be within 0..1.");
    if (configuration_.homing.method == 37
        || (configuration_.homing.method == 35 && !configuration_.homing.allow_current_position_zero))
        throw std::invalid_argument("Current-position homing requires explicit method-35 permission.");
    require_output(layout, controlword_, 16U, configuration_.name);
    require_output(layout, target_position_, 32U, configuration_.name);
    require_output(layout, velocity_offset_, 32U, configuration_.name);
    require_output(layout, mode_of_operation_, 8U, configuration_.name);
    require_input(layout, statusword_, 16U, configuration_.name);
    require_input(layout, position_actual_, 32U, configuration_.name);
    require_input(layout, velocity_actual_, 32U, configuration_.name);
    require_input(layout, mode_display_, 8U, configuration_.name);
    require_input(layout, error_code_, 16U, configuration_.name);
}

bool Cia402AxisAdapter::can_apply_command(const RealtimeCommand& command) const noexcept {
    auto setpoint = setpoint_;
    auto safety = safety_;
    switch (command.kind) {
        case RealtimeCommandKind::Enable:
            return !configuration_.commissioning_required && !safety_.status().faulted()
                && last_error_code_ == 0 && !homing_failed_
                && std::abs(last_actual_velocity_) <= configuration_.safety.maximum_actual_velocity;
        case RealtimeCommandKind::Disable:
        case RealtimeCommandKind::Stop: return true;
        case RealtimeCommandKind::Reset: return safety.reset_if_healthy();
        case RealtimeCommandKind::Home:
            return configuration_.homing.method != 0 && enable_requested_ && operation_enabled_
                && !motion_busy_ && !homing_failed_ && homing_phase_ == HomingPhase::Idle
                && last_mode_display_ == configuration_.operating_mode && last_error_code_ == 0
                && std::abs(last_actual_velocity_) <= configuration_.completion_velocity_tolerance
                && setpoint_.current().mode == MotionSetpointMode::Hold;
        case RealtimeCommandKind::MoveAbsolute:
            return !motion_busy_ && enable_requested_ && operation_enabled_
                && (!configuration_.homing.required || homed_)
                && homing_phase_ == HomingPhase::Idle && !homing_failed_
                && last_mode_display_ == configuration_.operating_mode && last_error_code_ == 0
                && setpoint.move_absolute(command.position, command.velocity);
        default: return false;
    }
}

bool Cia402AxisAdapter::apply_command(const RealtimeCommand& command) noexcept {
    switch (command.kind) {
        case RealtimeCommandKind::Enable:
            if (configuration_.commissioning_required || safety_.status().faulted()
                || last_error_code_ != 0U || homing_failed_
                || !(std::abs(last_actual_velocity_) <= configuration_.safety.maximum_actual_velocity)) return false;
            enable_requested_ = true;
            return true;
        case RealtimeCommandKind::Disable:
            if (motion_busy_) { motion_busy_ = false; motion_aborted_ = true; }
            enable_requested_ = false;
            homed_ = false;
            homing_phase_ = HomingPhase::Idle;
            setpoint_.stop();
            return true;
        case RealtimeCommandKind::Reset:
            if (!safety_.reset_if_healthy()) return false;
            if (motion_busy_) { motion_busy_ = false; motion_aborted_ = true; }
            reset_requested_ = true;
            enable_requested_ = false;
            homed_ = false;
            homing_failed_ = false;
            homing_phase_ = HomingPhase::Idle;
            setpoint_.stop();
            return true;
        case RealtimeCommandKind::Home:
            if (configuration_.homing.method == 0 || !enable_requested_
                || !operation_enabled_ || motion_busy_ || homing_failed_
                || homing_phase_ != HomingPhase::Idle
                || last_mode_display_ != configuration_.operating_mode
                || last_error_code_ != 0U
                || std::abs(last_actual_velocity_) > configuration_.completion_velocity_tolerance
                || setpoint_.current().mode != MotionSetpointMode::Hold) return false;
            homed_ = false;
            homing_cycles_ = 0;
            saw_homing_unattained_ = false;
            homing_phase_ = HomingPhase::SelectMode;
            return true;
        case RealtimeCommandKind::MoveAbsolute:
            if (motion_busy_ || !enable_requested_ || !operation_enabled_
                || (configuration_.homing.required && !homed_)
                || homing_phase_ != HomingPhase::Idle || homing_failed_
                || last_mode_display_ != configuration_.operating_mode
                || last_error_code_ != 0U
                || !setpoint_.move_absolute(command.position, command.velocity)) return false;
            peak_error_ = excess_ms_ = max_excess_ms_ = peak_actual_velocity_ = 0;
            motion_id_ = command.sequence;
            motion_target_ = command.position;
            settled_cycles_ = 0;
            motion_busy_ = true;
            motion_done_ = false;
            motion_aborted_ = false;
            return true;
        case RealtimeCommandKind::Jog:
            if (motion_busy_ || (configuration_.homing.required && !homed_)
                || homing_phase_ != HomingPhase::Idle || homing_failed_
                || last_mode_display_ != configuration_.operating_mode
                || last_error_code_ != 0U) return false;
            return enable_requested_ && operation_enabled_ && setpoint_.jog(command.velocity);
        case RealtimeCommandKind::Stop:
            if (motion_busy_) { motion_busy_ = false; motion_aborted_ = true; }
            if (homing_phase_ != HomingPhase::Idle) {
                homing_phase_ = HomingPhase::Idle;
                homed_ = false;
                enable_requested_ = false;
            }
            setpoint_.stop();
            return true;
        default:
            return false;
    }
}

bool Cia402AxisAdapter::can_start_group(double target, double minimum_position,
    double maximum_position, double path_velocity, double path_acceleration,
    double path_deceleration, double radius) const noexcept {
    const auto curvature_acceleration = radius > 0.0 ? path_velocity * path_velocity / radius : 0.0;
    return enable_requested_ && operation_enabled_ && !motion_busy_
        && (!configuration_.homing.required || homed_)
        && homing_phase_ == HomingPhase::Idle && !homing_failed_
        && last_mode_display_ == configuration_.operating_mode
        && last_error_code_ == 0U
        && !configuration_.commissioning_required && !safety_.status().faulted()
        && setpoint_.current().mode == MotionSetpointMode::Hold
        && std::isfinite(target) && std::isfinite(minimum_position)
        && std::isfinite(maximum_position) && std::isfinite(path_velocity)
        && std::isfinite(path_acceleration) && std::isfinite(path_deceleration)
        && path_velocity >= 0.0 && path_acceleration >= 0.0 && path_deceleration >= 0.0
        && minimum_position >= configuration_.motion.soft_limit_negative
        && maximum_position <= configuration_.motion.soft_limit_positive
        && path_velocity <= configuration_.motion.maximum_velocity
        && path_acceleration + curvature_acceleration <= configuration_.motion.maximum_acceleration
        && path_deceleration + curvature_acceleration <= configuration_.motion.maximum_deceleration;
}

double Cia402AxisAdapter::group_start_position() const noexcept {
    return setpoint_.current().position;
}

void Cia402AxisAdapter::begin_group(std::uint64_t motion_id, double target) noexcept {
    motion_id_ = motion_id;
    motion_target_ = target;
    settled_cycles_ = 0;
    motion_busy_ = true;
    motion_done_ = false;
    motion_aborted_ = false;
    if (!setpoint_.set_external(setpoint_.current().position, 0.0, false)) {
        motion_busy_ = false;
        motion_aborted_ = true;
    }
}

void Cia402AxisAdapter::group_setpoint(
    double position, double velocity, bool final_point) noexcept {
    if (!motion_busy_) return;
    if (!setpoint_.set_external(position, velocity, final_point)) {
        motion_busy_ = false;
        motion_aborted_ = true;
        setpoint_.stop();
    }
}

bool Cia402AxisAdapter::group_done(std::uint64_t motion_id) const noexcept {
    return motion_id_ == motion_id && motion_done_ && !motion_busy_;
}

bool Cia402AxisAdapter::group_faulted(std::uint64_t motion_id) const noexcept {
    return motion_id_ != motion_id || motion_aborted_ || safety_.status().faulted()
        || !enable_requested_ || !operation_enabled_;
}

void Cia402AxisAdapter::apply_safe_state() noexcept {
    if (motion_busy_) { motion_busy_ = false; motion_aborted_ = true; }
    enable_requested_ = false;
    reset_requested_ = false;
    homed_ = false;
    homing_phase_ = HomingPhase::Idle;
    setpoint_.stop();
}

void Cia402AxisAdapter::process(PdoImageView& image, RealtimeAxisSnapshot& snapshot) noexcept {
    snapshot.statusword = static_cast<std::uint16_t>(image.read_unsigned(statusword_));
    snapshot.mode_display = static_cast<std::int8_t>(image.read_signed(mode_display_));
    snapshot.error_code = static_cast<std::uint16_t>(image.read_unsigned(error_code_));
    last_error_code_ = snapshot.error_code;
    const auto raw_position = static_cast<std::int32_t>(image.read_signed(position_actual_));
    const auto raw_velocity = static_cast<std::int32_t>(image.read_signed(velocity_actual_));
    snapshot.actual_position = static_cast<double>(raw_position)
        / configuration_.counts_per_engineering_unit * configuration_.direction;
    snapshot.actual_velocity = static_cast<double>(raw_velocity)
        / configuration_.velocity_feedback_raw_per_unit_per_second * configuration_.direction;
    last_actual_velocity_ = snapshot.actual_velocity;
    snapshot.commissioning_required = configuration_.commissioning_required;
    operation_enabled_ = (snapshot.statusword & 0x006FU) == 0x0027U;
    last_mode_display_ = snapshot.mode_display;
    if (!operation_enabled_ || snapshot.error_code != 0U) homed_ = false;
    const auto homing_was_active = homing_phase_ != HomingPhase::Idle;
    if (homing_was_active) {
        if (homing_cycles_ < std::numeric_limits<std::uint32_t>::max()) ++homing_cycles_;
        const auto expired = static_cast<double>(homing_cycles_)
            * configuration_.motion.cycle_seconds > configuration_.homing.timeout_seconds;
        if (expired || !operation_enabled_ || snapshot.error_code != 0U
            || (snapshot.statusword & 0x2000U) != 0U) {
            homing_failed_ = true;
            homing_phase_ = HomingPhase::Idle;
            homed_ = false;
            enable_requested_ = false;
        } else if (homing_phase_ == HomingPhase::SelectMode && snapshot.mode_display == 6) {
            homing_phase_ = HomingPhase::Start;
        } else if (homing_phase_ == HomingPhase::Start && snapshot.mode_display == 6) {
            if ((snapshot.statusword & 0x1000U) == 0U) saw_homing_unattained_ = true;
            homing_phase_ = HomingPhase::Running;
        } else if (homing_phase_ == HomingPhase::Running && snapshot.mode_display == 6) {
            if ((snapshot.statusword & 0x1000U) == 0U) saw_homing_unattained_ = true;
            if (saw_homing_unattained_ && (snapshot.statusword & 0x1400U) == 0x1400U)
                homing_phase_ = HomingPhase::ReturnToCsp;
        } else if (homing_phase_ == HomingPhase::ReturnToCsp
                   && snapshot.mode_display == configuration_.operating_mode
                   && std::abs(snapshot.actual_velocity) <= configuration_.completion_velocity_tolerance) {
            homed_ = true;
            homing_phase_ = HomingPhase::Idle;
        }
    }
    if (!operation_enabled_ || !enable_requested_ || homing_was_active)
        setpoint_.synchronize(snapshot.actual_position);
    auto setpoint = setpoint_.update();
    // The PDO output still contains the last published target, before this cycle's write.
    // This fixes one-cycle look-ahead; it does not compensate unknown drive latency.
    snapshot.cycle_target = static_cast<double>(static_cast<std::int32_t>(image.read_signed(target_position_)))
        / configuration_.counts_per_engineering_unit * configuration_.direction;
    snapshot.cycle_error = snapshot.cycle_target - snapshot.actual_position;
    if (motion_busy_ && operation_enabled_ && enable_requested_) {
        peak_error_ = std::max(peak_error_, std::abs(snapshot.cycle_error));
        peak_actual_velocity_ = std::max(peak_actual_velocity_, std::abs(snapshot.actual_velocity));
        excess_ms_ = std::abs(snapshot.cycle_error) > configuration_.safety.maximum_following_error ? excess_ms_ + configuration_.motion.cycle_seconds * 1000 : 0;
        max_excess_ms_ = std::max(max_excess_ms_, excess_ms_);
    }
    snapshot.peak_error = peak_error_;
    snapshot.max_excess_ms = max_excess_ms_;
    snapshot.peak_actual_velocity = peak_actual_velocity_;
    snapshot.feedforward_velocity = setpoint.velocity * configuration_.velocity_feedforward_gain;
    const auto safety = safety_.update(snapshot.actual_position, snapshot.cycle_target,
        snapshot.actual_velocity, operation_enabled_ && enable_requested_
            && homing_phase_ == HomingPhase::Idle && !homing_was_active,
        (!configuration_.homing.required || homed_) && homing_phase_ == HomingPhase::Idle,
        (snapshot.statusword & 0x0004U) != 0U); // Includes Quick Stop Active while torque remains enabled.
    snapshot.software_limit_violation = safety.software_limit_violation;
    snapshot.following_error_fault = safety.following_error_fault;
    snapshot.overspeed_fault = safety.overspeed_fault;
    if (safety.faulted()) {
        enable_requested_ = false;
        homed_ = false;
        if (homing_was_active) homing_failed_ = true;
        homing_phase_ = HomingPhase::Idle;
        setpoint_.synchronize(snapshot.actual_position);
        setpoint = setpoint_.current();
    }
    if (motion_busy_) {
        if (!operation_enabled_ || !enable_requested_ || safety.faulted()
            || snapshot.error_code != 0U || (snapshot.statusword & 0x006FU) == 0x0008U) {
            motion_busy_ = false;
            motion_aborted_ = true;
        } else if (setpoint.mode == MotionSetpointMode::Hold
            && std::abs(snapshot.actual_position - motion_target_) <= configuration_.completion_position_tolerance
            && (configuration_.position_stability_completion
                || std::abs(snapshot.actual_velocity) <= configuration_.completion_velocity_tolerance)) {
            if (configuration_.position_stability_completion) {
                if (settled_cycles_ == 0) {
                    settled_position_min_ = snapshot.actual_position;
                    settled_position_max_ = snapshot.actual_position;
                } else {
                    settled_position_min_ = std::min(settled_position_min_, snapshot.actual_position);
                    settled_position_max_ = std::max(settled_position_max_, snapshot.actual_position);
                    if (settled_position_max_ - settled_position_min_
                        > configuration_.completion_velocity_tolerance * configuration_.completion_dwell_seconds) {
                        // Restart the full dwell at this sample after excessive excursion.
                        settled_cycles_ = 0;
                        settled_position_min_ = snapshot.actual_position;
                        settled_position_max_ = snapshot.actual_position;
                    }
                }
            }
            ++settled_cycles_;
            if (static_cast<double>(settled_cycles_) * configuration_.motion.cycle_seconds
                >= configuration_.completion_dwell_seconds) {
                motion_busy_ = false;
                motion_done_ = true;
            }
        } else {
            settled_cycles_ = 0;
        }
    }
    snapshot.motion_id = motion_id_;
    snapshot.motion_target = motion_target_;
    snapshot.motion_busy = motion_busy_;
    snapshot.motion_done = motion_done_;
    snapshot.motion_aborted = motion_aborted_;
    snapshot.homed = homed_;
    snapshot.homing_failed = homing_failed_;
    if (homing_failed_ || safety.faulted() || snapshot.error_code != 0U
        || (snapshot.statusword & 0x006FU) == 0x0008U) {
        snapshot.state = RealtimeAxisState::ErrorStop;
    } else if (homing_phase_ != HomingPhase::Idle) {
        snapshot.state = RealtimeAxisState::Homing;
    } else if (!operation_enabled_) {
        snapshot.state = RealtimeAxisState::Disabled;
    } else {
        switch (setpoint.mode) {
            case MotionSetpointMode::MoveAbsolute:
            case MotionSetpointMode::External:
                snapshot.state = RealtimeAxisState::DiscreteMotion;
                break;
            case MotionSetpointMode::Jog:
                snapshot.state = RealtimeAxisState::ContinuousMotion;
                break;
            case MotionSetpointMode::Stopping:
                snapshot.state = RealtimeAxisState::Stopping;
                break;
            case MotionSetpointMode::Hold:
                snapshot.state = RealtimeAxisState::Standstill;
                break;
        }
    }

    const auto raw_target_position = static_cast<std::int64_t>(std::llround(
        setpoint.position * configuration_.counts_per_engineering_unit * configuration_.direction));
    const auto raw_velocity_offset = static_cast<std::int64_t>(std::llround(
        setpoint.velocity * configuration_.velocity_feedforward_gain * configuration_.velocity_offset_raw_per_unit_per_second * configuration_.direction));
    image.write_from_controller(target_position_, static_cast<std::uint32_t>(
        std::clamp(raw_target_position,
            static_cast<std::int64_t>(std::numeric_limits<std::int32_t>::min()),
            static_cast<std::int64_t>(std::numeric_limits<std::int32_t>::max()))));
    image.write_from_controller(velocity_offset_, static_cast<std::uint32_t>(
        std::clamp(raw_velocity_offset,
            static_cast<std::int64_t>(std::numeric_limits<std::int32_t>::min()),
            static_cast<std::int64_t>(std::numeric_limits<std::int32_t>::max()))));
    image.write_from_controller(mode_of_operation_, static_cast<std::uint8_t>(
        homing_phase_ == HomingPhase::SelectMode || homing_phase_ == HomingPhase::Start
        || homing_phase_ == HomingPhase::Running ? 6 : configuration_.operating_mode));

    std::uint16_t controlword = 0U;
    if (reset_requested_) {
        controlword = 0x0080U;
        reset_requested_ = false; // CiA 402 fault reset is an edge, never a permanent level.
    } else if (enable_requested_ && !configuration_.commissioning_required) {
        switch (snapshot.statusword & 0x006FU) {
            case 0x0040U: controlword = 0x0006U; break; // shutdown
            case 0x0021U: controlword = 0x0007U; break; // switch on
            case 0x0023U:
            case 0x0027U: controlword = 0x000FU; break; // enable operation / hold enabled
            default: controlword = 0x0000U; break;
        }
        if (controlword == 0x000FU
            && (homing_phase_ == HomingPhase::Start
                || homing_phase_ == HomingPhase::Running))
            controlword |= 0x0010U;
    } else if ((snapshot.statusword & 0x006FU) == 0x0027U
               || (snapshot.statusword & 0x006FU) == 0x0023U) {
        controlword = 0x0006U;
    }
    image.write_from_controller(controlword_, controlword);
}

std::unique_ptr<RealtimeAxisAdapter> make_axis_adapter(
    const PdoLayout& layout, AxisFeedbackConfiguration configuration) {
    if (configuration.profile == "CiA402") {
        return std::make_unique<Cia402AxisAdapter>(layout, std::move(configuration));
    }
    throw std::invalid_argument("Unsupported axis profile '" + configuration.profile + "'.");
}

} // namespace ecmotion

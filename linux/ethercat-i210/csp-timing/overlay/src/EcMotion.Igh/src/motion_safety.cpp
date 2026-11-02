#include "motion_safety.hpp"

#include <cmath>
#include <limits>
#include <stdexcept>

namespace ecmotion {
namespace {
bool positive_finite(double value) noexcept {
    return std::isfinite(value) && value > 0.0;
}
}

MotionSafetyMonitor::MotionSafetyMonitor(MotionSafetyConfiguration configuration)
    : configuration_(configuration) {
    if (!positive_finite(configuration_.cycle_seconds)
        || !std::isfinite(configuration_.soft_limit_negative)
        || !std::isfinite(configuration_.soft_limit_positive)
        || configuration_.soft_limit_negative >= configuration_.soft_limit_positive
        || !positive_finite(configuration_.maximum_actual_velocity)
        || !positive_finite(configuration_.maximum_following_error)
        || !positive_finite(configuration_.following_error_timeout_seconds)) {
        throw std::invalid_argument("Motion safety configuration is invalid.");
    }
    const auto trip_cycles=std::ceil(configuration_.following_error_timeout_seconds
        / configuration_.cycle_seconds);
    if (!std::isfinite(trip_cycles)
        || trip_cycles>=static_cast<double>(std::numeric_limits<std::size_t>::max()))
        throw std::invalid_argument("Following-error timeout exceeds cycle counter range.");
    following_error_trip_cycles_ = static_cast<std::size_t>(trip_cycles);
    if (following_error_trip_cycles_ == 0U) following_error_trip_cycles_ = 1U;
}

MotionSafetyStatus MotionSafetyMonitor::update(
    double actual_position, double target_position, double actual_velocity,
    bool following_error_monitoring_enabled, bool referenced_position,
    bool velocity_monitoring_enabled) noexcept {
    current_.software_limit_violation = !std::isfinite(actual_position)
        || (referenced_position && (actual_position < configuration_.soft_limit_negative
            || actual_position > configuration_.soft_limit_positive));
    current_.overspeed_fault = !std::isfinite(actual_velocity)
        || (velocity_monitoring_enabled
            && std::abs(actual_velocity) > configuration_.maximum_actual_velocity);
    const auto following_error_exceeded = !std::isfinite(target_position)
        || std::abs(target_position - actual_position) > configuration_.maximum_following_error;
    if (following_error_monitoring_enabled && following_error_exceeded) {
        if (following_error_cycles_ < following_error_trip_cycles_) ++following_error_cycles_;
    } else {
        following_error_cycles_ = 0U;
    }
    current_.following_error_fault = following_error_monitoring_enabled
        && following_error_cycles_ >= following_error_trip_cycles_;
    latched_.software_limit_violation |= current_.software_limit_violation;
    latched_.overspeed_fault |= current_.overspeed_fault;
    latched_.following_error_fault |= current_.following_error_fault;
    return latched_;
}

bool MotionSafetyMonitor::reset_if_healthy() noexcept {
    if (current_.software_limit_violation || current_.overspeed_fault
        || current_.following_error_fault) return false;
    latched_ = {};
    following_error_cycles_ = 0U;
    return true;
}

} // namespace ecmotion

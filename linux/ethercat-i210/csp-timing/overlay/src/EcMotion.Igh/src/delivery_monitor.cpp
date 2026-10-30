#include "delivery_monitor.hpp"

#include <bit>
#include <limits>
#include <stdexcept>

namespace csp_timing {
namespace {
std::int32_t signed_delta(std::uint32_t lhs, std::uint32_t rhs) noexcept {
    return std::bit_cast<std::int32_t>(static_cast<std::uint32_t>(lhs - rhs));
}
} // namespace

DeliveryMonitor::DeliveryMonitor(DeliveryConfig config) : config_(config) {
    constexpr auto max_step = static_cast<std::uint64_t>(std::numeric_limits<std::int32_t>::max());
    if (config.period_ns == 0 || config.period_ns > max_step / 2 ||
        config.sync_shift_ns >= config.period_ns ||
        config.boundary_guard_ns >= config.period_ns ||
        config.boundary_guard_ns >= config.period_ns - config.boundary_guard_ns ||
        config.remaining_send_budget_ns == 0 ||
        config.remaining_send_budget_ns >= config.period_ns ||
        config.remaining_send_budget_ns > config.period_ns - 2 * config.boundary_guard_ns ||
        config.required_good_cycles == 0) {
        throw std::invalid_argument("invalid delivery timing budget");
    }
}

DeliveryDiagnostics DeliveryMonitor::observe(const DeliveryInput& in) noexcept {
    DeliveryDiagnostics d;
    // Compare before subtracting/adding: hostile or startup metadata must never
    // wrap into an apparently ordered timestamp or an apparently safe phase.
    d.metadata_valid = in.current_cycle != 0 &&
        in.previous_send_cycle == in.current_cycle - 1 &&
        in.current_scheduled_ns >= in.previous_scheduled_ns &&
        in.current_scheduled_ns - in.previous_scheduled_ns == config_.period_ns &&
        in.previous_tx_call_start_ns >= in.previous_scheduled_ns &&
        in.previous_tx_call_start_ns < in.current_scheduled_ns &&
        in.current_rx_ns >= in.current_scheduled_ns &&
        in.current_rx_ns - in.current_scheduled_ns < config_.period_ns &&
        in.current_rx_ns - in.current_scheduled_ns <
            config_.period_ns - config_.remaining_send_budget_ns &&
        in.current_rx_ns <= std::numeric_limits<std::uint64_t>::max() -
                                config_.remaining_send_budget_ns;
    if (d.metadata_valid && in.reference_valid) {
        d.reference_phase_ns = signed_delta(in.ref_time_low32,
            static_cast<std::uint32_t>(in.previous_scheduled_ns));
        d.reference_phase_valid = true;
        const auto elapsed = in.current_rx_ns - in.previous_tx_call_start_ns;
        const auto predicted_reference = static_cast<std::uint32_t>(in.ref_time_low32 +
            static_cast<std::uint32_t>(elapsed) +
            static_cast<std::uint32_t>(config_.remaining_send_budget_ns));
        d.predicted_phase_ns = signed_delta(predicted_reference,
            static_cast<std::uint32_t>(in.current_scheduled_ns));
        d.predicted_phase_valid = true;
        if (have_reference_) {
            d.reference_step_ns = signed_delta(in.ref_time_low32, last_reference_);
            d.reference_step_valid = in.current_cycle > last_cycle_ &&
                in.current_cycle - last_cycle_ == 1 &&
                in.current_scheduled_ns >= last_scheduled_ns_ &&
                in.current_scheduled_ns - last_scheduled_ns_ == config_.period_ns &&
                d.reference_step_ns > 0 &&
                static_cast<std::uint64_t>(d.reference_step_ns) <= 2 * config_.period_ns;
        }
        const auto lower = static_cast<std::int64_t>(config_.sync_shift_ns) -
            static_cast<std::int64_t>(config_.period_ns) +
            static_cast<std::int64_t>(config_.boundary_guard_ns);
        const auto upper = static_cast<std::int64_t>(config_.sync_shift_ns) -
            static_cast<std::int64_t>(config_.boundary_guard_ns);
        d.valid = d.reference_step_valid &&
            d.reference_phase_ns >= lower && d.reference_phase_ns <= upper &&
            d.predicted_phase_ns >= lower && d.predicted_phase_ns <= upper;
        // Retain a usable association even after a bad phase/step, so startup
        // recovery can establish a new consecutive run without an external reset.
        last_cycle_ = in.current_cycle;
        last_scheduled_ns_ = in.current_scheduled_ns;
        last_reference_ = in.ref_time_low32;
        have_reference_ = true;
    } else {
        have_reference_ = false;
    }
    if (!d.valid) good_cycles_ = 0;
    else if (good_cycles_ < config_.required_good_cycles) ++good_cycles_;
    d.good_cycles = good_cycles_;
    d.ready = good_cycles_ == config_.required_good_cycles;
    return d;
}

void DeliveryMonitor::reset() noexcept {
    good_cycles_ = 0;
    last_cycle_ = 0;
    last_scheduled_ns_ = 0;
    last_reference_ = 0;
    have_reference_ = false;
}
} // namespace csp_timing

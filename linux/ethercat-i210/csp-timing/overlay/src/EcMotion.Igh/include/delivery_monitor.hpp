#pragma once

#include <cstdint>

namespace csp_timing {
struct DeliveryConfig {
    std::uint64_t period_ns{1000000};
    std::uint64_t sync_shift_ns{500000};
    std::uint64_t boundary_guard_ns{200000};
    std::uint64_t remaining_send_budget_ns{100000};
    std::uint64_t required_good_cycles{1000};
};
struct DeliveryInput {
    std::uint64_t current_cycle{};
    std::uint64_t current_scheduled_ns{};
    std::uint64_t current_rx_ns{};
    bool reference_valid{};
    std::uint32_t ref_time_low32{};
    std::uint64_t previous_send_cycle{};
    std::uint64_t previous_scheduled_ns{};
    std::uint64_t previous_tx_call_start_ns{};
};
struct DeliveryDiagnostics {
    bool metadata_valid{};
    bool reference_phase_valid{};
    bool predicted_phase_valid{};
    bool reference_step_valid{};
    bool valid{};
    bool ready{};
    std::int32_t reference_phase_ns{};
    std::int32_t predicted_phase_ns{};
    std::int32_t reference_step_ns{};
    std::uint64_t good_cycles{};
};
// Single-thread-owned R2M/same-epoch diagnostic, never a motion command.
// IgH reference belongs to the previous send; phase is signed, never period modulo.
// Setup may throw. observe/reset allocate nothing and call no OS or IgH APIs.
// RX plus the remaining send budget must finish strictly within the host cycle.
// First observation establishes a baseline. Invalid input drops readiness without
// latching a fault; callers must separately latch operational faults as required.
class DeliveryMonitor {
public:
    explicit DeliveryMonitor(DeliveryConfig config = {});
    [[nodiscard]] DeliveryDiagnostics observe(const DeliveryInput& input) noexcept;
    void reset() noexcept;
private:
    DeliveryConfig config_;
    std::uint64_t good_cycles_{};
    std::uint64_t last_cycle_{};
    std::uint64_t last_scheduled_ns_{};
    std::uint32_t last_reference_{};
    bool have_reference_{};
};
} // namespace csp_timing

#include "delivery_monitor.hpp"

#include <cstdlib>
#include <iostream>
#include <limits>
#include <stdexcept>

namespace {
using namespace csp_timing;
void require(bool value, const char* message) {
    if (!value) { std::cerr << "FAIL: " << message << '\n'; std::exit(1); }
}
DeliveryInput sample(std::uint64_t cycle, std::uint64_t previous = 10000000,
                     std::int32_t phase = 50000) {
    return {cycle, previous + 1000000, previous + 1050000, true,
            static_cast<std::uint32_t>(previous) + static_cast<std::uint32_t>(phase),
            cycle - 1, previous, previous + 50000};
}
DeliveryDiagnostics prime(DeliveryMonitor& monitor, std::int32_t phase = 50000,
                          std::uint64_t previous = 10000000) {
    auto first = monitor.observe(sample(1, previous, phase));
    require(first.metadata_valid && first.reference_phase_valid &&
            first.predicted_phase_valid && !first.reference_step_valid &&
            !first.valid && first.good_cycles == 0, "first frame establishes baseline");
    return monitor.observe(sample(2, previous + 1000000, phase));
}
void phases_and_wrap() {
    for (auto phase : {-50000, 50000}) {
        DeliveryMonitor monitor;
        auto d = prime(monitor, phase);
        require(d.valid && d.reference_phase_ns == phase, "signed phase preserved");
        require(d.predicted_phase_ns == phase + 100000, "current send prediction");
        require(d.reference_step_ns == 1000000, "reference step is one period");
    }
    DeliveryMonitor monitor;
    auto d = prime(monitor, 50000, 0xffffffffULL - 500000);
    require(d.valid && d.reference_phase_ns == 50000, "low32 wrap remains valid");
}
void lock_drop_recovery() {
    DeliveryConfig config; config.required_good_cycles = 2;
    DeliveryMonitor monitor(config);
    require(!prime(monitor).ready, "one valid frame insufficient");
    auto d = monitor.observe(sample(3, 12000000));
    require(d.valid && d.ready && d.good_cycles == 2, "consecutive frames lock");
    auto bad = sample(4, 13000000); bad.reference_valid = false;
    d = monitor.observe(bad);
    require(!d.valid && !d.ready && d.good_cycles == 0, "invalid frame drops lock");
    require(!monitor.observe(sample(5, 14000000)).valid, "recovery starts baseline");
    require(!monitor.observe(sample(6, 15000000)).ready, "recovery needs full count");
    require(monitor.observe(sample(7, 16000000)).ready, "automatic startup recovery");
    require(monitor.observe(sample(8, 17000000)).good_cycles == 2, "good count saturates");
    monitor.reset();
    require(!monitor.observe(sample(1)).valid, "explicit reset clears baseline");
}
void rejects_phase_and_prediction() {
    for (int fault = 0; fault < 5; ++fault) {
        DeliveryMonitor monitor;
        require(prime(monitor).valid, "phase rejection setup");
        auto in = sample(3, 12000000);
        if (fault == 0) in.ref_time_low32 += 1000000;
        if (fault == 1) in.ref_time_low32 -= 1000000;
        if (fault == 2) in.current_rx_ns += 250000;
        if (fault == 3) { in.ref_time_low32 = 11710000; in.previous_tx_call_start_ns = 12200000; in.current_rx_ns = 13000000; }
        if (fault == 4) in.ref_time_low32 += 300001;
        auto d = monitor.observe(in);
        require(!d.valid && !d.ready && d.good_cycles == 0, "unsafe phase rejected");
        require(d.reference_phase_valid && d.predicted_phase_valid, "unsafe phase diagnostics retained");
        if (fault == 0) require(d.reference_phase_ns == 1050000, "whole missing cycle not folded");
        if (fault == 1) require(d.reference_phase_ns == -950000, "negative missing cycle not folded");
        if (fault == 2) require(d.predicted_phase_ns == 400000, "late prediction retained");
        if (fault == 3) require(d.predicted_phase_ns == -390000, "early prediction retained");
    }
    for (auto phase : {-300000, 200000}) {
        DeliveryMonitor monitor;
        require(prime(monitor, phase).valid, "inclusive delivery window boundaries");
    }
}
void rejects_bad_reference_steps() {
    for (int fault = 0; fault < 4; ++fault) {
        DeliveryMonitor monitor; require(prime(monitor).valid, "step setup");
        auto in = sample(3, 12000000);
        if (fault == 0) in.ref_time_low32 = 11050000;
        if (fault == 1) in.ref_time_low32 = 11049999;
        if (fault == 2) in.ref_time_low32 = 13050001;
        if (fault == 3) { in.current_cycle = 4; in.previous_send_cycle = 3; }
        auto d = monitor.observe(in);
        require(!d.valid && !d.reference_step_valid, "duplicate backward excessive or discontinuous reference rejected");
    }
}
void rejects_metadata_and_overflow() {
    for (int fault = 0; fault < 10; ++fault) {
        DeliveryMonitor monitor; require(prime(monitor).valid, "metadata setup");
        auto in = sample(3, 12000000);
        if (fault == 0) in.previous_send_cycle = 1;
        if (fault == 1) in.current_cycle = 0;
        if (fault == 2) in.previous_scheduled_ns = in.current_scheduled_ns;
        if (fault == 3) in.previous_tx_call_start_ns = in.previous_scheduled_ns - 1;
        if (fault == 4) in.previous_tx_call_start_ns = in.current_scheduled_ns;
        if (fault == 5) in.current_rx_ns = in.current_scheduled_ns - 1;
        if (fault == 6) in.current_rx_ns = in.current_scheduled_ns + 1000000;
        if (fault == 7) in.current_scheduled_ns += 1;
        if (fault == 8) in.previous_scheduled_ns = std::numeric_limits<std::uint64_t>::max();
        if (fault == 9) {
            in = sample(3, std::numeric_limits<std::uint64_t>::max() - 1100000);
            in.current_rx_ns = std::numeric_limits<std::uint64_t>::max();
        }
        auto d = monitor.observe(in);
        require(!d.valid && !d.metadata_valid && !d.ready && d.good_cycles == 0,
                "invalid metadata or time arithmetic overflow rejected");
    }
    DeliveryMonitor monitor;
    auto in = sample(1); in.reference_valid = false;
    auto d = monitor.observe(in);
    require(!d.valid && !d.reference_phase_valid && !d.predicted_phase_valid,
            "invalid reference cannot expose plausible phase values");
}
void rejects_host_budget_overrun() {
    for (auto receive_offset : {900000ULL, 950000ULL}) {
        DeliveryMonitor monitor; require(prime(monitor).valid, "host deadline setup");
        auto in = sample(3, 12000000);
        in.previous_tx_call_start_ns = in.previous_scheduled_ns + 900000;
        in.current_rx_ns = in.current_scheduled_ns + receive_offset;
        const auto d = monitor.observe(in);
        require(!d.valid && !d.ready && !d.metadata_valid && d.good_cycles == 0,
                "remaining send budget must finish strictly before host cycle end");
    }
}
void invalid_config() {
    for (int fault = 0; fault < 10; ++fault) {
        DeliveryConfig c;
        if (fault == 0) c.period_ns = 0;
        if (fault == 1) c.sync_shift_ns = c.period_ns;
        if (fault == 2) c.boundary_guard_ns = 500000;
        if (fault == 3) c.remaining_send_budget_ns = 0;
        if (fault == 4) c.remaining_send_budget_ns = 600001;
        if (fault == 5) c.required_good_cycles = 0;
        if (fault == 6) c.period_ns = std::numeric_limits<std::uint64_t>::max();
        if (fault == 7) c.boundary_guard_ns = std::numeric_limits<std::uint64_t>::max();
        if (fault == 8) c.period_ns = 1073741824;
        if (fault == 9) { c.boundary_guard_ns = 0; c.remaining_send_budget_ns = c.period_ns; }
        bool threw = false;
        try { DeliveryMonitor monitor(c); } catch (const std::invalid_argument&) { threw = true; }
        require(threw, "impossible monitor configuration rejected");
    }
}
} // namespace
int main() {
    phases_and_wrap(); lock_drop_recovery(); rejects_phase_and_prediction();
    rejects_bad_reference_steps(); rejects_metadata_and_overflow();
    rejects_host_budget_overrun(); invalid_config();
    std::cout << "delivery_monitor_selftest: PASS\n";
}

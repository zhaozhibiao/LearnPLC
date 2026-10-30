// SPDX-License-Identifier: MIT
#pragma once
#include "pdo_image.hpp"
#include "realtime_exchange.hpp"
#include "delivery_monitor.hpp"
#include "demand_guard.hpp"
#include <array>
#include <atomic>
#include <iosfwd>
#include <span>
#include <string>
#include <vector>

namespace ecmotion {
struct CyclicTiming {
    std::uint64_t rx_start_ns{},rx_end_ns{},process_end_ns{},tx_start_ns{},tx_end_ns{};
    // IgH returns reference time from the preceding sync datagram, not this send.
    std::uint64_t reference_cycle{},reference_tx_start_ns{};
    std::uint32_t reference_time_low32{};
    bool reference_valid{};
    std::uint64_t reference_scheduled_ns{},pre_send_check_ns{};
    bool cycle_timing_enabled{},motion_timing_ready{true};
    bool pre_send_budget_overrun{},actual_send_budget_overrun{},send_host_deadline_overrun{};
    csp_timing::DeliveryDiagnostics delivery{};
};
struct CyclicAxisTraceFrame {
    std::uint64_t cycle{},scheduled_ns{},monotonic_ns{},motion_id{};
    bool bus_ready{};
    std::uint8_t state{};
    std::int32_t previous_target_raw{},sent_target_raw{},offset_raw{},actual_raw{},velocity_raw{},demand_raw{},drive_error_raw{};
    std::int16_t torque_raw{};
    std::uint16_t controlword{},statusword{},error_code{};
    std::int8_t mode{};
    double cycle_target{},cycle_error{},actual_position{},actual_velocity{};
    CyclicTiming timing{};
    DemandResult demand_shadow{};
    bool homed{};
};

// Single RT producer and one non-RT control/export consumer. Read/export only frozen or after producer stops.
class CyclicAxisTrace final {
public:
    static constexpr std::size_t maximum_capacity=120000;
    CyclicAxisTrace(const PdoLayout&,const std::string& prefix,std::size_t capacity);
    [[nodiscard]] std::int32_t target_raw(const PdoImageView&) const noexcept;
    void capture(const PdoImageView&,const RealtimeAxisSnapshot&,std::uint64_t cycle,
        std::uint64_t scheduled_ns,std::uint64_t monotonic_ns,bool bus_ready,
        std::int32_t previous_target_raw,const CyclicTiming& timing={}) noexcept;
    [[nodiscard]] bool request_freeze() noexcept;
    [[nodiscard]] bool frozen() const noexcept;
    [[nodiscard]] bool request_rearm() noexcept;
    [[nodiscard]] std::span<const CyclicAxisTraceFrame> frames() const noexcept {return {storage_.data(),size_};}
    [[nodiscard]] std::uint64_t dropped() const noexcept {return dropped_;}
    void write_csv(std::ostream&) const;
    // Never overwrite an existing file or follow a leaf symlink. Non-RT only.
    void write_csv_exclusive(const std::string& path) const;
private:
    enum Channel:std::size_t {Target,Offset,Actual,Velocity,Demand,DriveError,Torque,Controlword,Statusword,ErrorCode,Mode,Count};
    DemandGuard demand_guard_;
    std::array<PdoHandle,Count> handles_{};
    std::vector<CyclicAxisTraceFrame> storage_;
    std::size_t size_{};
    std::uint64_t dropped_{};
    enum class State:unsigned {Recording,FreezeRequested,Frozen,RearmRequested};
    static_assert(std::atomic<State>::is_always_lock_free);
    std::atomic<State> state_{State::Recording};
};
}

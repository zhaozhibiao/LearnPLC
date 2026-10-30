#pragma once

#include "pdo_image.hpp"
#include "realtime_exchange.hpp"

namespace ecmotion {

// Vendor-neutral contract used by the realtime cycle. Vendor/profile selection
// happens once at startup; the 1 ms path never branches on a drive brand.
class RealtimeAxisAdapter {
public:
    virtual ~RealtimeAxisAdapter() = default;
    [[nodiscard]] virtual bool apply_command(const RealtimeCommand& command) noexcept = 0;
    [[nodiscard]] virtual bool can_apply_command(const RealtimeCommand&) const noexcept { return false; }
    [[nodiscard]] virtual bool can_start_group(double target, double minimum_position,
        double maximum_position, double path_velocity,
        double path_acceleration, double path_deceleration, double radius) const noexcept {
        static_cast<void>(target); static_cast<void>(minimum_position);
        static_cast<void>(maximum_position); static_cast<void>(path_velocity);
        static_cast<void>(path_acceleration); static_cast<void>(path_deceleration);
        static_cast<void>(radius); return false;
    }
    [[nodiscard]] virtual double group_start_position() const noexcept { return 0.0; }
    virtual void begin_group(std::uint64_t motion_id, double target) noexcept {
        static_cast<void>(motion_id); static_cast<void>(target);
    }
    virtual void group_setpoint(double position, double velocity, bool final_point) noexcept {
        static_cast<void>(position); static_cast<void>(velocity); static_cast<void>(final_point);
    }
    [[nodiscard]] virtual bool group_done(std::uint64_t motion_id) const noexcept {
        static_cast<void>(motion_id); return false;
    }
    [[nodiscard]] virtual bool group_faulted(std::uint64_t motion_id) const noexcept {
        static_cast<void>(motion_id); return true;
    }
    virtual void set_motion_timing_ready(bool) noexcept {}
    virtual void apply_safe_state() noexcept = 0;
    virtual void process(PdoImageView& image, RealtimeAxisSnapshot& snapshot) noexcept = 0;
};

} // namespace ecmotion

#pragma once

namespace ecmotion {

struct MotionSetpointConfiguration {
    double cycle_seconds{};
    double soft_limit_negative{};
    double soft_limit_positive{};
    double maximum_velocity{};
    double maximum_acceleration{};
    double maximum_deceleration{};
    double maximum_jerk{};
    bool profile_defaults_known{true};
};

enum class MotionSetpointMode {
    Hold,
    MoveAbsolute,
    Jog,
    Stopping,
    External
};

struct MotionSetpoint {
    double position{};
    double velocity{};
    MotionSetpointMode mode{MotionSetpointMode::Hold};
};

// Drive-independent cyclic setpoint generator. It uses engineering units only;
// PDO conversion belongs to the selected drive/profile adapter.
// maximum_jerk>0 selects rest-to-rest quintic MoveAbsolute. Explicit v2 with
// configured J=0 uses an analytic A/D bounded trapezoid; absent-version legacy
// retains the original cyclic trapezoid. Reentry into rest profiles is rejected
// until Hold. V2 Jog/ordinary Stop use bounded cubic velocity transitions.
// Legacy Jog/Stop and protective paths retain their semantics. Synchronize discards curves.
class MotionSetpointGenerator final {
public:
    explicit MotionSetpointGenerator(MotionSetpointConfiguration configuration);

    void synchronize(double actual_position) noexcept;
    [[nodiscard]] bool move_absolute(double position, double velocity, double acceleration = 0.0,
        double deceleration = 0.0, double jerk = 0.0, bool profile_v2 = false) noexcept;
    [[nodiscard]] bool jog(double velocity) noexcept;
    void stop() noexcept;
    [[nodiscard]] bool jog_profile(double velocity, double acceleration, double deceleration, double jerk) noexcept;
    [[nodiscard]] bool stop_profile(double deceleration, double jerk) noexcept;
    [[nodiscard]] bool set_external(double position, double velocity, bool final_point) noexcept;
    [[nodiscard]] MotionSetpoint update() noexcept;
    [[nodiscard]] const MotionSetpoint& current() const noexcept { return current_; }

private:
    [[nodiscard]] double approach(double value, double target, double rate) const noexcept;

    struct VelocityCurve {
        double x{}, v{}, a{}, b{}, c{}, duration{};
        double position(double t) const noexcept;
        double velocity(double t) const noexcept;
    };
    [[nodiscard]] bool velocity_curve(double target, double acceleration, double deceleration,
        double jerk, VelocityCurve& curve) const noexcept;
    [[nodiscard]] bool curve_inside_limits(const VelocityCurve& curve) const noexcept;
    void begin_velocity_curve(const VelocityCurve& curve, double target, double acceleration,
        double deceleration, double jerk, bool stopping) noexcept;
    VelocityCurve velocity_curve_;
    bool velocity_profile_{};
    double velocity_elapsed_{};
    double acceleration_{};
    double active_jerk_{};
    MotionSetpointConfiguration configuration_;
    MotionSetpoint current_;
    double active_acceleration_{};
    double active_deceleration_{};
    double target_position_{};
    double commanded_velocity_{};
    bool synchronized_{};
    double smooth_start_{};
    double smooth_elapsed_{};
    double smooth_duration_{};
    bool trapezoid_profile_{};
    double trapezoid_peak_{}, trapezoid_accel_time_{}, trapezoid_cruise_time_{};
};

} // namespace ecmotion

#pragma once

#include "realtime_axis_adapter.hpp"
#include "motion_setpoint.hpp"
#include "motion_safety.hpp"

#include <array>
#include <memory>
#include <string>
#include <vector>

namespace ecmotion {

struct AxisFeedbackConfiguration {
    struct HomingConfiguration {
        bool required{true};
        // Zero means physically unconfigured: Home is rejected and motion stays inhibited.
        std::int8_t method{};
        // Deliberate bench-only permission for method 35; never establishes a reference by itself.
        bool allow_current_position_zero{};
        std::uint32_t search_speed_raw{};
        std::uint32_t latch_speed_raw{};
        std::uint32_t acceleration_raw{};
        std::int32_t offset_raw{};
        double timeout_seconds{30.0};
    };
    std::string name;
    int slave_position{};
    std::string profile;
    std::string position_unit;
    std::string controlword_key;
    std::string target_position_key;
    std::string velocity_offset_key;
    std::string mode_of_operation_key;
    std::string statusword_key;
    std::string position_actual_key;
    std::string velocity_actual_key;
    std::string mode_display_key;
    std::string error_code_key;
    double counts_per_engineering_unit{};
    int direction{};
    bool commissioning_required{};
    std::int8_t operating_mode{8};
    MotionSetpointConfiguration motion;
    MotionSafetyConfiguration safety;
    HomingConfiguration homing;
    double completion_position_tolerance{0.02};
    double completion_velocity_tolerance{0.1};
    double completion_dwell_seconds{0.02};
    // Optional bounded-position dwell for drives with quantized velocity feedback.
    // Raw velocity feedback and homing/safety velocity checks are unchanged.
    bool position_stability_completion{};
    double velocity_feedforward_gain{1.0};
    // Zero in programmatic legacy configurations means inherit position scale.
    double velocity_feedback_raw_per_unit_per_second{};
    double velocity_offset_raw_per_unit_per_second{};

    // Explicit bench-only reference change; false preserves raw host-target supervision.
    bool drive_demand_protection{};

    static AxisFeedbackConfiguration load(const std::string& path, const std::string& axis_name);
    static std::vector<AxisFeedbackConfiguration> load_all(const std::string& path);
};

class Cia402AxisAdapter final : public RealtimeAxisAdapter {
public:
    Cia402AxisAdapter(const PdoLayout& layout, AxisFeedbackConfiguration configuration);
    [[nodiscard]] bool apply_command(const RealtimeCommand& command) noexcept override;
    [[nodiscard]] bool can_apply_command(const RealtimeCommand& command) const noexcept override;
    [[nodiscard]] bool can_start_group(double target, double minimum_position,
        double maximum_position, double path_velocity,
        double path_acceleration, double path_deceleration, double radius) const noexcept override;
    [[nodiscard]] double group_start_position() const noexcept override;
    void begin_group(std::uint64_t motion_id, double target) noexcept override;
    void group_setpoint(double position, double velocity, bool final_point) noexcept override;
    [[nodiscard]] bool group_done(std::uint64_t motion_id) const noexcept override;
    [[nodiscard]] bool group_faulted(std::uint64_t motion_id) const noexcept override;
    void set_motion_timing_ready(bool ready) noexcept override;
    void apply_safe_state() noexcept override;
    void process(PdoImageView& image, RealtimeAxisSnapshot& snapshot) noexcept override;

private:
    AxisFeedbackConfiguration configuration_;
    PdoHandle controlword_;
    PdoHandle target_position_;
    PdoHandle velocity_offset_;
    PdoHandle mode_of_operation_;
    PdoHandle statusword_;
    PdoHandle position_actual_;
    PdoHandle velocity_actual_;
    PdoHandle mode_display_;
    PdoHandle error_code_;
    PdoHandle position_demand_;
    PdoHandle drive_following_error_;
    // Entry image target is sent[n-1]; two entries back is drive demand sent[n-3].
    std::array<std::int32_t, 2> demand_history_{};
    unsigned demand_history_size_{}, demand_history_next_{};
    bool demand_flow_fault_{}, demand_reset_healthy_{true};
    double peak_error_{}, excess_ms_{}, max_excess_ms_{}, peak_actual_velocity_{};
    bool motion_timing_ready_{true};
    bool motion_timing_fault_{};
    bool enable_requested_{};
    bool reset_requested_{};
    bool operation_enabled_{};
    std::int8_t last_mode_display_{};
    double last_actual_velocity_{};
    std::uint16_t last_error_code_{};
    MotionSetpointGenerator setpoint_;
    MotionSafetyMonitor safety_;
    std::uint64_t motion_id_{};
    double motion_target_{};
    std::uint32_t settled_cycles_{};
    double settled_position_min_{};
    double settled_position_max_{};
    bool motion_busy_{};
    bool motion_done_{};
    bool motion_aborted_{};
    bool homed_{};
    bool homing_failed_{};
    enum class HomingPhase : std::uint8_t { Idle, SelectMode, Start, Running, ReturnToCsp };
    HomingPhase homing_phase_{HomingPhase::Idle};
    std::uint32_t homing_cycles_{};
    bool saw_homing_unattained_{};
};

[[nodiscard]] std::unique_ptr<RealtimeAxisAdapter> make_axis_adapter(
    const PdoLayout& layout, AxisFeedbackConfiguration configuration);

} // namespace ecmotion

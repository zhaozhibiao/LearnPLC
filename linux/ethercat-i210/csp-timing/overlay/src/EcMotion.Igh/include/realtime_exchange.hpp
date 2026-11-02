#pragma once

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>

namespace ecmotion {

enum class RealtimeCommandKind : std::uint8_t {
    Enable,
    Disable,
    Reset,
    Home,
    MoveAbsolute,
    Jog,
    Stop,
    WriteOutputs,
    GroupLine,
    GroupArc,
    GroupSpatialLine,
    GroupSpatialArc,
    WriteScalarOutput,
    StartPath
};

// One bounded, preloaded execution. No file/JSON/heap access from the RT consumer.
struct RealtimePath {
    static constexpr std::size_t maximum_points = 1024;
    std::uint16_t count{};
    std::uint8_t axis_count{};
    std::array<std::uint16_t, 4> axes{};
    std::array<std::array<double, 4>, maximum_points> points{};
    std::array<double, 4> minimum{}, maximum{};
    double velocity{}, acceleration{}, deceleration{};
};

struct RealtimeCommand {
    std::uint64_t sequence{};
    RealtimeCommandKind kind{};
    std::uint16_t axis_index{};
    std::uint16_t secondary_axis_index{};
    std::uint16_t third_axis_index{};
    std::uint16_t fourth_axis_index{};
    std::uint8_t group_axis_count{2};
    double position{};
    double secondary_position{};
    double third_position{};
    double fourth_position{};
    double velocity{};
    double acceleration{};
    double deceleration{};
    double jerk{};
    std::uint8_t motion_profile_version{};
    double center_x{};
    double center_y{};
    double center_z{};
    double normal_x{};
    double normal_y{};
    double normal_z{1.0};
    bool clockwise{};
    std::uint64_t output_mask{};
    std::uint64_t output_values{};
    std::uint16_t scalar_index{};
    std::uint32_t scalar_raw_bits{};
};

struct RealtimeBatch {
    struct ScalarWrite { std::uint16_t index{}; std::uint32_t raw_bits{}; };
    std::array<RealtimeCommand, 16> commands{};
    std::uint16_t count{};
    std::uint64_t deadline_ns{};
    std::uint64_t sequence{};
    std::uint64_t io_output_mask{};
    std::uint64_t io_output_values{};
    std::array<ScalarWrite, 512> scalar_writes{};
    std::uint16_t scalar_write_count{};
};

enum class RealtimeAxisState : std::uint8_t {
    Disabled,
    Standstill,
    Homing,
    DiscreteMotion,
    ContinuousMotion,
    Stopping,
    ErrorStop
};

struct RealtimeAxisSnapshot {
    bool motion_timing_ready{true};
    bool motion_timing_fault{};
    double cycle_target{};
    double cycle_error{};
    double peak_error{};
    double max_excess_ms{};
    double feedforward_velocity{};
    double peak_actual_velocity{};
    double demand_position_error{};
    double drive_following_error{};
    double demand_delivery_error{};
    bool demand_feedback_valid{};
    bool demand_delivery_mismatch{};
    bool demand_delivery_fault{};

    RealtimeAxisState state{RealtimeAxisState::Disabled};
    double actual_position{};
    double actual_velocity{};
    bool homed{};
    bool homing_failed{};
    std::uint16_t statusword{};
    std::int8_t mode_display{};
    std::uint16_t error_code{};
    bool commissioning_required{};
    bool software_limit_violation{};
    bool following_error_fault{};
    bool overspeed_fault{};
    std::uint64_t motion_id{};
    double motion_target{};
    bool motion_busy{};
    bool motion_done{};
    bool motion_aborted{};
};

struct RealtimeSnapshot {
    std::uint64_t path_execution_id{};
    std::uint16_t path_completed_points{}, path_total_points{};
    std::uint8_t path_status{}; // 0 none, 1 running, 2 done, 3 aborted
    static constexpr std::size_t maximum_axes = 256;
    static constexpr std::size_t maximum_scalar_channels = 512;
    struct Scalar {
        double engineering_value{};
        std::uint32_t raw_bits{};
        std::uint8_t quality{2}; // 0 good, 1 commissioning required, 2 invalid raw
        std::uint8_t encoding{}; // 1 INT16, 2 FLOAT32
        bool output{};
    };
    std::array<Scalar, maximum_scalar_channels> scalars{};
    std::uint16_t scalar_count{};
    std::array<RealtimeAxisSnapshot, maximum_axes> axes{};
    std::uint16_t axis_count{};
    RealtimeAxisState state{RealtimeAxisState::Disabled};
    double actual_position{};
    double actual_velocity{};
    bool homed{};
    std::uint16_t statusword{};
    std::int8_t mode_display{};
    std::uint16_t error_code{};
    bool commissioning_required{};
    bool software_limit_violation{};
    bool following_error_fault{};
    bool overspeed_fault{};
    bool bus_ready{};
    std::uint64_t digital_inputs{};
    std::uint64_t digital_outputs{};
    std::uint64_t cycles{};
    std::uint64_t sample_monotonic_ns{};
    std::uint64_t last_applied_command_sequence{};
    std::uint64_t last_applied_command_cycle{};
    std::uint64_t rejected_commands{};
};

class RealtimeExchange final {
public:
    static constexpr std::size_t command_capacity = 64;

    [[nodiscard]] std::uint64_t next_command_sequence() noexcept;
    [[nodiscard]] bool try_enqueue(RealtimeCommand command) noexcept;
    [[nodiscard]] bool try_dequeue(RealtimeCommand& command) noexcept;
    [[nodiscard]] bool try_enqueue_batch(const RealtimeBatch& batch) noexcept;
    [[nodiscard]] bool try_dequeue_batch(RealtimeBatch& batch) noexcept;
    [[nodiscard]] bool prepare_path(const RealtimePath& path, std::uint64_t sequence) noexcept;
    [[nodiscard]] const RealtimePath* ready_path(std::uint64_t sequence) const noexcept;
    void activate_path() noexcept;
    void finish_path() noexcept;

    void complete(std::uint64_t sequence, bool accepted) noexcept;
    void cancel_through(std::uint64_t sequence) noexcept;
    [[nodiscard]] bool is_cancelled(std::uint64_t sequence) const noexcept;
    [[nodiscard]] std::uint64_t completed_sequence() const noexcept;
    [[nodiscard]] std::uint64_t rejected_sequence() const noexcept;

    [[nodiscard]] std::uint64_t request_safe_state() noexcept;
    [[nodiscard]] bool consume_safe_state_request() noexcept;
    void acknowledge_safe_state() noexcept;
    [[nodiscard]] std::uint64_t safe_state_epoch() const noexcept;

    void publish(const RealtimeSnapshot& snapshot) noexcept;
    [[nodiscard]] RealtimeSnapshot snapshot() const noexcept;

private:
    // 0 free, 1 producer writes, 2 ready, 3 RT active. Only RT releases enqueued paths.
    std::atomic_uint8_t path_state_{};
    RealtimePath path_{};
    std::uint64_t path_sequence_{};
    std::array<RealtimeBatch, command_capacity> commands_{};
    alignas(64) std::atomic_size_t write_index_{};
    alignas(64) std::atomic_size_t read_index_{};
    std::atomic_uint64_t next_sequence_{1};
    std::atomic_uint64_t completed_sequence_{};
    std::atomic_uint64_t rejected_sequence_{};
    std::atomic_uint64_t cancelled_through_{};
    std::atomic_bool safe_state_requested_{};
    std::atomic_uint64_t safe_state_epoch_{};

    std::atomic_uint64_t snapshot_sequence_{};
    std::atomic_uint64_t path_execution_id_{};
    std::atomic_uint16_t path_completed_points_{}, path_total_points_{};
    std::atomic_uint8_t path_status_{};
    struct AtomicAxis {
        std::atomic_bool motion_timing_ready{true};
        std::atomic_bool motion_timing_fault{};
        std::atomic<double> cycle_target{};
        std::atomic<double> cycle_error{};
        std::atomic<double> peak_error{};
        std::atomic<double> max_excess_ms{};
        std::atomic<double> feedforward_velocity{};
        std::atomic<double> peak_actual_velocity{};
        std::atomic<double> demand_position_error{};
        std::atomic<double> drive_following_error{};
        std::atomic<double> demand_delivery_error{};
        std::atomic_bool demand_feedback_valid{};
        std::atomic_bool demand_delivery_mismatch{};
        std::atomic_bool demand_delivery_fault{};

        std::atomic<RealtimeAxisState> state{RealtimeAxisState::Disabled};
        std::atomic<double> position{};
        std::atomic<double> velocity{};
        std::atomic_bool homed{};
        std::atomic_uint16_t statusword{};
        std::atomic<std::int8_t> mode{};
        std::atomic_uint16_t error{};
        std::atomic_bool commissioning{};
        std::atomic_bool limit{};
        std::atomic_bool following{};
        std::atomic_bool overspeed{};
        std::atomic_uint64_t motion_id{};
        std::atomic<double> motion_target{};
        std::atomic_bool motion_busy{};
        std::atomic_bool motion_done{};
        std::atomic_bool motion_aborted{};
    };
    std::array<AtomicAxis, RealtimeSnapshot::maximum_axes> axes_{};
    struct AtomicScalar {
        std::atomic<double> engineering_value{};
        std::atomic_uint32_t raw_bits{};
        std::atomic_uint8_t quality{2};
        std::atomic_uint8_t encoding{};
        std::atomic_bool output{};
    };
    std::array<AtomicScalar, RealtimeSnapshot::maximum_scalar_channels> scalars_{};
    std::atomic_uint16_t scalar_count_{};
    std::atomic_uint16_t axis_count_{};
    std::atomic<RealtimeAxisState> state_{RealtimeAxisState::Disabled};
    std::atomic<double> actual_position_{};
    std::atomic<double> actual_velocity_{};
    std::atomic_bool homed_{};
    std::atomic_uint16_t statusword_{};
    std::atomic<std::int8_t> mode_display_{};
    std::atomic_uint16_t error_code_{};
    std::atomic_bool commissioning_required_{};
    std::atomic_bool software_limit_violation_{};
    std::atomic_bool following_error_fault_{};
    std::atomic_bool overspeed_fault_{};
    std::atomic_bool bus_ready_{};
    std::atomic_uint64_t digital_inputs_{};
    std::atomic_uint64_t digital_outputs_{};
    std::atomic_uint64_t cycles_{};
    std::atomic_uint64_t sample_monotonic_ns_{};
    std::atomic_uint64_t last_applied_command_sequence_{};
    std::atomic_uint64_t last_applied_command_cycle_{};
    std::atomic_uint64_t rejected_commands_{};
};

} // namespace ecmotion

#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <vector>

namespace csp_timing {

struct DelayConfig {
    bool enabled{false};
    std::int64_t delay_ns{0};
    std::int64_t min_delay_ns{0};
    std::int64_t max_delay_ns{0};
};

struct ReferenceEvaluation {
    std::int64_t raw_actual_position;
    std::optional<long double> raw_error;
    std::optional<long double> aligned_reference;
    std::optional<long double> aligned_error;
};

// Single-thread-owned diagnostic history. Allocate at setup, before entering RT.
// Store only positions actually published, with their monotonic application time.
// Reset explicitly on homing, epoch/counter reset or recovery from invalid input.
class ReferenceHistory {
public:
    ReferenceHistory(std::size_t capacity, std::uint64_t max_gap_ns);
    bool publish(std::uint64_t cycle_id, std::uint64_t application_time_ns,
                 std::int64_t position, bool fresh = true) noexcept;
    [[nodiscard]] std::optional<long double> position_at(std::uint64_t time_ns) const noexcept;

    // receive_time_ns is the host reception observation in the same monotonic
    // epoch as the history. Explicit calibrated delay estimates the feedback
    // reference time. Independently observed delay validates that model; it must
    // never be fitted by minimizing position error. Call boundaries alone do
    // not establish wire/application/feedback timing. This returns diagnostics,
    // never a corrected actual position or a control target.
    [[nodiscard]] ReferenceEvaluation evaluate(
        std::int64_t raw_actual_position, std::uint64_t receive_time_ns,
        std::int64_t independently_observed_delay_ns, const DelayConfig& config) const noexcept;
    void reset() noexcept;
    [[nodiscard]] bool faulted() const noexcept { return faulted_; }
    [[nodiscard]] std::size_t size() const noexcept { return size_; }

private:
    struct Sample {
        std::uint64_t cycle_id;
        std::uint64_t time_ns;
        std::int64_t position;
    };
    [[nodiscard]] const Sample& at(std::size_t offset) const noexcept;
    std::vector<Sample> samples_;
    std::uint64_t max_gap_ns_;
    std::size_t oldest_{0};
    std::size_t size_{0};
    bool faulted_{false};
};

} // namespace csp_timing

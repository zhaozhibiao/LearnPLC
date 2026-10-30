#include "reference_history.hpp"

#include <limits>
#include <stdexcept>

namespace csp_timing {

ReferenceHistory::ReferenceHistory(std::size_t capacity, std::uint64_t max_gap_ns)
    : samples_(capacity), max_gap_ns_(max_gap_ns) {
    if (capacity < 2 || max_gap_ns == 0)
        throw std::invalid_argument("reference history needs capacity >= 2 and a positive gap bound");
}

const ReferenceHistory::Sample& ReferenceHistory::at(std::size_t offset) const noexcept {
    // Avoid adding potentially very large size_t values before taking modulo.
    const auto remaining = samples_.size() - oldest_;
    const auto index = offset < remaining ? oldest_ + offset : offset - remaining;
    return samples_[index];
}

bool ReferenceHistory::publish(std::uint64_t cycle_id, std::uint64_t application_time_ns,
                               std::int64_t position, bool fresh) noexcept {
    if (faulted_) return false;
    bool valid = fresh;
    if (size_ != 0) {
        const auto& previous = at(size_ - 1);
        valid = valid && previous.cycle_id != std::numeric_limits<std::uint64_t>::max()
            && cycle_id == previous.cycle_id + 1
            && application_time_ns > previous.time_ns;
        // Subtract only after strict ordering is established.
        if (valid) valid = application_time_ns - previous.time_ns <= max_gap_ns_;
    }
    if (!valid) {
        faulted_ = true;
        return false;
    }
    std::size_t index;
    if (size_ == samples_.size()) {
        index = oldest_;
        oldest_ = oldest_ == samples_.size() - 1 ? 0 : oldest_ + 1;
    } else {
        const auto remaining = samples_.size() - oldest_;
        index = size_ < remaining ? oldest_ + size_ : size_ - remaining;
        ++size_;
    }
    samples_[index] = {cycle_id, application_time_ns, position};
    return true;
}

std::optional<long double> ReferenceHistory::position_at(std::uint64_t time_ns) const noexcept {
    if (faulted_ || size_ == 0 || time_ns < at(0).time_ns || time_ns > at(size_ - 1).time_ns)
        return std::nullopt;
    for (std::size_t i = 0; i < size_; ++i) {
        const auto& next = at(i);
        if (time_ns == next.time_ns) return static_cast<long double>(next.position);
        if (time_ns < next.time_ns) {
            const auto& previous = at(i - 1);
            const auto elapsed = time_ns - previous.time_ns;
            const auto gap = next.time_ns - previous.time_ns;
            const auto fraction = static_cast<long double>(elapsed) / static_cast<long double>(gap);
            return static_cast<long double>(previous.position)
                + (static_cast<long double>(next.position) - static_cast<long double>(previous.position))
                    * fraction;
        }
    }
    return std::nullopt;
}

ReferenceEvaluation ReferenceHistory::evaluate(
    std::int64_t raw_actual_position, std::uint64_t receive_time_ns,
    std::int64_t independently_observed_delay_ns, const DelayConfig& config) const noexcept {
    ReferenceEvaluation result{raw_actual_position, std::nullopt, std::nullopt, std::nullopt};
    // Preserve raw diagnostics even when the alignment model/history is invalid.
    if (size_ != 0)
        result.raw_error = static_cast<long double>(at(size_ - 1).position)
            - static_cast<long double>(raw_actual_position);
    if (!config.enabled || config.min_delay_ns < 0 || config.max_delay_ns < config.min_delay_ns
        || config.delay_ns < config.min_delay_ns || config.delay_ns > config.max_delay_ns
        || independently_observed_delay_ns < config.min_delay_ns
        || independently_observed_delay_ns > config.max_delay_ns)
        return result;
    const auto delay = static_cast<std::uint64_t>(config.delay_ns);
    if (receive_time_ns < delay) return result;
    result.aligned_reference = position_at(receive_time_ns - delay);
    if (result.aligned_reference)
        result.aligned_error = *result.aligned_reference - static_cast<long double>(raw_actual_position);
    return result;
}

void ReferenceHistory::reset() noexcept {
    oldest_ = 0;
    size_ = 0;
    faulted_ = false;
}

} // namespace csp_timing

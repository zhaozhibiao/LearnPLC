#include "realtime_exchange.hpp"

#include <limits>

namespace ecmotion {

bool RealtimeExchange::prepare_path(const RealtimePath& path, std::uint64_t sequence) noexcept {
    std::uint8_t expected = 0;
    if (!path_state_.compare_exchange_strong(expected, 1, std::memory_order_acquire)) return false;
    path_ = path; path_sequence_ = sequence;
    path_state_.store(2, std::memory_order_release);
    return true;
}
const RealtimePath* RealtimeExchange::ready_path(std::uint64_t sequence) const noexcept {
    return path_state_.load(std::memory_order_acquire) >= 2 && path_sequence_ == sequence ? &path_ : nullptr;
}
void RealtimeExchange::activate_path() noexcept { path_state_.store(3, std::memory_order_release); }
void RealtimeExchange::finish_path() noexcept { path_state_.store(0, std::memory_order_release); }

static_assert(std::atomic<double>::is_always_lock_free,
    "The target must provide lock-free atomic double snapshots.");
static_assert(std::atomic<std::uint64_t>::is_always_lock_free,
    "The target must provide lock-free 64-bit command and IO atomics.");
static_assert(std::atomic<std::uint16_t>::is_always_lock_free,
    "The target must provide lock-free 16-bit axis count atomics.");

std::uint64_t RealtimeExchange::next_command_sequence() noexcept {
    return next_sequence_.fetch_add(1, std::memory_order_relaxed);
}

bool RealtimeExchange::try_enqueue(RealtimeCommand command) noexcept {
    RealtimeBatch batch;
    batch.count = 1;
    batch.commands[0] = command;
    return try_enqueue_batch(batch);
}

bool RealtimeExchange::try_enqueue_batch(const RealtimeBatch& batch) noexcept {
    if (batch.count > batch.commands.size() || batch.scalar_write_count > batch.scalar_writes.size()
        || (batch.count == 0 && (!batch.sequence || (!batch.io_output_mask && !batch.scalar_write_count)))) return false;
    const auto write = write_index_.load(std::memory_order_relaxed);
    const auto next = (write + 1U) % command_capacity;
    if (next == read_index_.load(std::memory_order_acquire)) return false;
    commands_[write] = batch;
    write_index_.store(next, std::memory_order_release);
    return true;
}

bool RealtimeExchange::try_dequeue(RealtimeCommand& command) noexcept {
    const auto read = read_index_.load(std::memory_order_relaxed);
    if (read == write_index_.load(std::memory_order_acquire) || commands_[read].count != 1) return false;
    RealtimeBatch batch;
    if (!try_dequeue_batch(batch)) return false;
    command = batch.commands[0];
    return true;
}

bool RealtimeExchange::try_dequeue_batch(RealtimeBatch& batch) noexcept {
    const auto read = read_index_.load(std::memory_order_relaxed);
    if (read == write_index_.load(std::memory_order_acquire)) return false;
    batch = commands_[read];
    read_index_.store((read + 1U) % command_capacity, std::memory_order_release);
    return true;
}

void RealtimeExchange::complete(std::uint64_t sequence, bool accepted) noexcept {
    if (!accepted) rejected_sequence_.store(sequence, std::memory_order_relaxed);
    completed_sequence_.store(sequence, std::memory_order_release);
}

void RealtimeExchange::cancel_through(std::uint64_t sequence) noexcept {
    auto current = cancelled_through_.load(std::memory_order_relaxed);
    while (sequence > current
           && !cancelled_through_.compare_exchange_weak(
               current, sequence, std::memory_order_release, std::memory_order_relaxed)) {
    }
}

bool RealtimeExchange::is_cancelled(std::uint64_t sequence) const noexcept {
    return sequence <= cancelled_through_.load(std::memory_order_acquire);
}

std::uint64_t RealtimeExchange::completed_sequence() const noexcept {
    return completed_sequence_.load(std::memory_order_acquire);
}

std::uint64_t RealtimeExchange::rejected_sequence() const noexcept {
    return rejected_sequence_.load(std::memory_order_relaxed);
}

std::uint64_t RealtimeExchange::request_safe_state() noexcept {
    const auto expected_epoch = safe_state_epoch_.load(std::memory_order_acquire) + 1U;
    safe_state_requested_.store(true, std::memory_order_release);
    return expected_epoch;
}

bool RealtimeExchange::consume_safe_state_request() noexcept {
    return safe_state_requested_.exchange(false, std::memory_order_acq_rel);
}

void RealtimeExchange::acknowledge_safe_state() noexcept {
    safe_state_epoch_.fetch_add(1, std::memory_order_release);
}

std::uint64_t RealtimeExchange::safe_state_epoch() const noexcept {
    return safe_state_epoch_.load(std::memory_order_acquire);
}

void RealtimeExchange::publish(const RealtimeSnapshot& value) noexcept {
    snapshot_sequence_.fetch_add(1, std::memory_order_acq_rel);
    path_execution_id_.store(value.path_execution_id,std::memory_order_relaxed);
    path_completed_points_.store(value.path_completed_points,std::memory_order_relaxed);
    path_total_points_.store(value.path_total_points,std::memory_order_relaxed);
    path_status_.store(value.path_status,std::memory_order_relaxed);
    const auto scalar_count = static_cast<std::uint16_t>(
        value.scalar_count > scalars_.size() ? scalars_.size() : value.scalar_count);
    scalar_count_.store(scalar_count, std::memory_order_relaxed);
    for (std::size_t i = 0; i < scalar_count; ++i) {
        const auto& source = value.scalars[i];
        auto& target = scalars_[i];
        target.engineering_value.store(source.engineering_value, std::memory_order_relaxed);
        target.raw_bits.store(source.raw_bits, std::memory_order_relaxed);
        target.quality.store(source.quality, std::memory_order_relaxed);
        target.encoding.store(source.encoding, std::memory_order_relaxed);
        target.output.store(source.output, std::memory_order_relaxed);
    }
    axis_count_.store(value.axis_count, std::memory_order_relaxed);
    for (std::size_t i = 0; i < value.axis_count; ++i) {
        const auto& source = value.axes[i];
        auto& target = axes_[i];
        target.motion_timing_ready.store(source.motion_timing_ready,std::memory_order_relaxed);
        target.motion_timing_fault.store(source.motion_timing_fault,std::memory_order_relaxed);
        target.cycle_target.store(source.cycle_target, std::memory_order_relaxed);
        target.cycle_error.store(source.cycle_error, std::memory_order_relaxed);
        target.peak_error.store(source.peak_error, std::memory_order_relaxed);
        target.max_excess_ms.store(source.max_excess_ms, std::memory_order_relaxed);
        target.feedforward_velocity.store(source.feedforward_velocity, std::memory_order_relaxed);
        target.peak_actual_velocity.store(source.peak_actual_velocity, std::memory_order_relaxed);
        target.state.store(source.state, std::memory_order_relaxed);
        target.position.store(source.actual_position, std::memory_order_relaxed);
        target.velocity.store(source.actual_velocity, std::memory_order_relaxed);
        target.homed.store(source.homed, std::memory_order_relaxed);
        target.statusword.store(source.statusword, std::memory_order_relaxed);
        target.mode.store(source.mode_display, std::memory_order_relaxed);
        target.error.store(source.error_code, std::memory_order_relaxed);
        target.commissioning.store(source.commissioning_required, std::memory_order_relaxed);
        target.limit.store(source.software_limit_violation, std::memory_order_relaxed);
        target.following.store(source.following_error_fault, std::memory_order_relaxed);
        target.overspeed.store(source.overspeed_fault, std::memory_order_relaxed);
        target.motion_id.store(source.motion_id, std::memory_order_relaxed);
        target.motion_target.store(source.motion_target, std::memory_order_relaxed);
        target.motion_busy.store(source.motion_busy, std::memory_order_relaxed);
        target.motion_done.store(source.motion_done, std::memory_order_relaxed);
        target.motion_aborted.store(source.motion_aborted, std::memory_order_relaxed);
    }
    state_.store(value.state, std::memory_order_relaxed);
    actual_position_.store(value.actual_position, std::memory_order_relaxed);
    actual_velocity_.store(value.actual_velocity, std::memory_order_relaxed);
    homed_.store(value.homed, std::memory_order_relaxed);
    statusword_.store(value.statusword, std::memory_order_relaxed);
    mode_display_.store(value.mode_display, std::memory_order_relaxed);
    error_code_.store(value.error_code, std::memory_order_relaxed);
    commissioning_required_.store(value.commissioning_required, std::memory_order_relaxed);
    software_limit_violation_.store(value.software_limit_violation, std::memory_order_relaxed);
    following_error_fault_.store(value.following_error_fault, std::memory_order_relaxed);
    overspeed_fault_.store(value.overspeed_fault, std::memory_order_relaxed);
    bus_ready_.store(value.bus_ready, std::memory_order_relaxed);
    digital_inputs_.store(value.digital_inputs, std::memory_order_relaxed);
    digital_outputs_.store(value.digital_outputs, std::memory_order_relaxed);
    cycles_.store(value.cycles, std::memory_order_relaxed);
    sample_monotonic_ns_.store(value.sample_monotonic_ns, std::memory_order_relaxed);
    last_applied_command_sequence_.store(value.last_applied_command_sequence,
        std::memory_order_relaxed);
    last_applied_command_cycle_.store(value.last_applied_command_cycle,
        std::memory_order_relaxed);
    rejected_commands_.store(value.rejected_commands, std::memory_order_relaxed);
    snapshot_sequence_.fetch_add(1, std::memory_order_release);
}

RealtimeSnapshot RealtimeExchange::snapshot() const noexcept {
    RealtimeSnapshot value;
    while (true) {
        const auto before = snapshot_sequence_.load(std::memory_order_acquire);
        value.path_execution_id=path_execution_id_.load(std::memory_order_relaxed);
        value.path_completed_points=path_completed_points_.load(std::memory_order_relaxed);
        value.path_total_points=path_total_points_.load(std::memory_order_relaxed);
        value.path_status=path_status_.load(std::memory_order_relaxed);
        if ((before & 1U) != 0U) continue;
        value.scalar_count = scalar_count_.load(std::memory_order_relaxed);
        for (std::size_t i = 0; i < value.scalar_count; ++i) {
            const auto& source = scalars_[i];
            auto& target = value.scalars[i];
            target.engineering_value = source.engineering_value.load(std::memory_order_relaxed);
            target.raw_bits = source.raw_bits.load(std::memory_order_relaxed);
            target.quality = source.quality.load(std::memory_order_relaxed);
            target.encoding = source.encoding.load(std::memory_order_relaxed);
            target.output = source.output.load(std::memory_order_relaxed);
        }
        value.axis_count = axis_count_.load(std::memory_order_relaxed);
        for (std::size_t i = 0; i < value.axis_count; ++i) {
            const auto& source = axes_[i];
            auto& target = value.axes[i];
            target.motion_timing_ready=source.motion_timing_ready.load(std::memory_order_relaxed);
            target.motion_timing_fault=source.motion_timing_fault.load(std::memory_order_relaxed);
            target.cycle_target = source.cycle_target.load(std::memory_order_relaxed);
            target.cycle_error = source.cycle_error.load(std::memory_order_relaxed);
            target.peak_error = source.peak_error.load(std::memory_order_relaxed);
            target.max_excess_ms = source.max_excess_ms.load(std::memory_order_relaxed);
            target.feedforward_velocity = source.feedforward_velocity.load(std::memory_order_relaxed);
            target.peak_actual_velocity = source.peak_actual_velocity.load(std::memory_order_relaxed);
            target.state = source.state.load(std::memory_order_relaxed);
            target.actual_position = source.position.load(std::memory_order_relaxed);
            target.actual_velocity = source.velocity.load(std::memory_order_relaxed);
            target.homed = source.homed.load(std::memory_order_relaxed);
            target.statusword = source.statusword.load(std::memory_order_relaxed);
            target.mode_display = source.mode.load(std::memory_order_relaxed);
            target.error_code = source.error.load(std::memory_order_relaxed);
            target.commissioning_required = source.commissioning.load(std::memory_order_relaxed);
            target.software_limit_violation = source.limit.load(std::memory_order_relaxed);
            target.following_error_fault = source.following.load(std::memory_order_relaxed);
            target.overspeed_fault = source.overspeed.load(std::memory_order_relaxed);
            target.motion_id = source.motion_id.load(std::memory_order_relaxed);
            target.motion_target = source.motion_target.load(std::memory_order_relaxed);
            target.motion_busy = source.motion_busy.load(std::memory_order_relaxed);
            target.motion_done = source.motion_done.load(std::memory_order_relaxed);
            target.motion_aborted = source.motion_aborted.load(std::memory_order_relaxed);
        }
        value.state = state_.load(std::memory_order_relaxed);
        value.actual_position = actual_position_.load(std::memory_order_relaxed);
        value.actual_velocity = actual_velocity_.load(std::memory_order_relaxed);
        value.homed = homed_.load(std::memory_order_relaxed);
        value.statusword = statusword_.load(std::memory_order_relaxed);
        value.mode_display = mode_display_.load(std::memory_order_relaxed);
        value.error_code = error_code_.load(std::memory_order_relaxed);
        value.commissioning_required = commissioning_required_.load(std::memory_order_relaxed);
        value.software_limit_violation = software_limit_violation_.load(std::memory_order_relaxed);
        value.following_error_fault = following_error_fault_.load(std::memory_order_relaxed);
        value.overspeed_fault = overspeed_fault_.load(std::memory_order_relaxed);
        value.bus_ready = bus_ready_.load(std::memory_order_relaxed);
        value.digital_inputs = digital_inputs_.load(std::memory_order_relaxed);
        value.digital_outputs = digital_outputs_.load(std::memory_order_relaxed);
        value.cycles = cycles_.load(std::memory_order_relaxed);
        value.sample_monotonic_ns = sample_monotonic_ns_.load(std::memory_order_relaxed);
        value.last_applied_command_sequence = last_applied_command_sequence_.load(
            std::memory_order_relaxed);
        value.last_applied_command_cycle = last_applied_command_cycle_.load(
            std::memory_order_relaxed);
        value.rejected_commands = rejected_commands_.load(std::memory_order_relaxed);
        const auto after = snapshot_sequence_.load(std::memory_order_acquire);
        if (before == after && (after & 1U) == 0U) return value;
    }
}

} // namespace ecmotion

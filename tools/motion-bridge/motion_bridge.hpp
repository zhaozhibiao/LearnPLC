#pragma once
#include <array>
#include <atomic>
#include <cstdint>
#include <type_traits>

namespace workbench_motion {
constexpr std::uint64_t transport_error = 1009;
// Little-endian dedicated virtual IPC. Explicit fields avoid compiler padding.
struct Command {
    std::uint64_t request{}, path{}, version{};
    std::array<char, 64> hash{};
    std::uint64_t scan{}, flags{};
};
struct Feedback {
    std::uint64_t request{}, busy{}, done{}, error{};
    double progress{};
};
static_assert(sizeof(Command) == 104 && sizeof(Feedback) == 40);
static_assert(std::atomic<unsigned>::is_always_lock_free,
              "scan mailbox requires lock-free index atomics");

// Bounded SPSC mailbox, matching the original RealtimeExchange ownership split.
// Producer and consumer have distinct indices. No allocator, mutex or socket in scan.
template<class T, unsigned N = 16> class Mailbox {
    static_assert(std::is_trivially_copyable<T>::value && N > 1);
    std::array<T, N> slots_{};
    std::atomic<unsigned> write_{0}, read_{0};
public:
    bool push(const T& value) noexcept {
        auto w = write_.load(std::memory_order_relaxed);
        auto next = (w + 1) % N;
        if (next == read_.load(std::memory_order_acquire)) return false;
        slots_[w] = value;
        write_.store(next, std::memory_order_release);
        return true;
    }
    bool pop(T& value) noexcept {
        auto r = read_.load(std::memory_order_relaxed);
        if (r == write_.load(std::memory_order_acquire)) return false;
        value = slots_[r];
        read_.store((r + 1) % N, std::memory_order_release);
        return true;
    }
};

bool start(const char* isolated_socket);
void stop(); // lifecycle only; never call from a cyclic scan
bool submit(const Command&) noexcept;
bool poll(Feedback&) noexcept;
}

extern "C" {
bool workbench_motion_start(const char* isolated_socket);
void workbench_motion_after_scan(bool plc_running);
void workbench_motion_stop();
}

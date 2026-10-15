#pragma once
#include <array>
#include <atomic>
#include <cstdint>
#include <type_traits>
namespace workbench_axis {
constexpr unsigned max_axes = 64;
constexpr std::uint64_t transport_error = 2004;
struct CommandSlot { std::uint64_t request{},kind{}; double position{},velocity{}; std::uint64_t flags{}; };
struct CommandPacket { std::uint64_t scan{},flags{}; std::array<CommandSlot,max_axes> axes{}; };
struct FeedbackSlot { std::uint64_t request{},busy{},done{},error{},aborted{},enabled{},homed{},state{},motion_id{}; double position{},velocity{}; };
struct FeedbackPacket { std::array<FeedbackSlot,max_axes> axes{}; };
static_assert(sizeof(CommandSlot)==40 && sizeof(CommandPacket)==2576);
static_assert(sizeof(FeedbackSlot)==88 && sizeof(FeedbackPacket)==5632);
static_assert(std::atomic<unsigned>::is_always_lock_free && std::atomic<bool>::is_always_lock_free);
template<class T,unsigned N=32> class Mailbox {
    static_assert(std::is_trivially_copyable<T>::value && N>1);
    std::array<T,N> slots_{};
    std::atomic<unsigned> write_{0},read_{0};
public:
    bool push(const T& v) noexcept {
        auto w=write_.load(std::memory_order_relaxed),next=(w+1)%N;
        if(next==read_.load(std::memory_order_acquire))return false;
        slots_[w]=v; write_.store(next,std::memory_order_release); return true;
    }
    bool pop(T& v) noexcept {
        auto r=read_.load(std::memory_order_relaxed);
        if(r==write_.load(std::memory_order_acquire))return false;
        v=slots_[r];read_.store((r+1)%N,std::memory_order_release);return true;
    }
};
bool start(const char* private_socket);
void stop(); // outside scan, before unload
bool submit(const CommandPacket&) noexcept;
bool poll(FeedbackPacket&) noexcept;
}
extern "C" {
bool workbench_axis_start(const char* private_socket);
void workbench_axis_stop();
void workbench_axis_after_scan(bool scan_success);
bool workbench_axis_bootstrap(); // defined once by axis_lifecycle.inc
}


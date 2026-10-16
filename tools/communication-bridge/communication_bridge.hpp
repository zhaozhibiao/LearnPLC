#pragma once
// SPDX-License-Identifier: MIT
#include <array>
#include <atomic>
#include <cstdint>
#include <type_traits>
namespace workbench_comm {
constexpr unsigned max_channels=16, max_data=512;
struct CommandSlot { std::uint64_t request,action,function,unit,address,quantity,read_address,read_quantity,length; std::array<std::uint8_t,max_data> data; };
struct CommandPacket { std::uint64_t scan,flags; std::array<CommandSlot,max_channels> channels; };
struct FeedbackSlot { std::uint64_t request,busy,done,error,unknown,acknowledged,length; std::array<std::uint8_t,max_data> data; };
struct FeedbackPacket { std::array<FeedbackSlot,max_channels> channels; };
static_assert(sizeof(CommandPacket)==9360 && sizeof(FeedbackPacket)==9088);
template<typename T,unsigned N> class Mailbox {
 static_assert(std::is_trivially_copyable_v<T>); std::array<T,N> values_{}; std::atomic<unsigned> read_{0},write_{0};
public:
 bool push(const T& value) noexcept { auto w=write_.load(std::memory_order_relaxed),next=(w+1)%N; if(next==read_.load(std::memory_order_acquire))return false; values_[w]=value;write_.store(next,std::memory_order_release);return true; }
 bool pop(T& value) noexcept { auto r=read_.load(std::memory_order_relaxed);if(r==write_.load(std::memory_order_acquire))return false;value=values_[r];read_.store((r+1)%N,std::memory_order_release);return true; }
};
bool start(const char*); void stop(); bool submit(const CommandPacket&) noexcept; bool poll(FeedbackPacket&) noexcept;
}
extern "C" { bool workbench_comm_start(const char*); void workbench_comm_stop(); void workbench_comm_after_scan(bool); }

#pragma once
#include <array>
#include <cstddef>
#include <cstdint>

namespace ecmotion::generated {
enum class Direction : std::uint8_t { Rx, Tx };
struct Entry { std::uint16_t index; std::uint8_t subindex; std::uint8_t bits; const char* key; };
struct Pdo { Direction direction; std::uint8_t sync_manager; std::uint16_t index; std::size_t entry_begin; std::size_t entry_count; };
struct Dc { bool enabled; std::uint16_t assign_activate; std::uint32_t sync0_cycle_ns; std::int32_t sync0_shift_ns; std::uint32_t sync1_cycle_ns; };
struct Slave { std::uint16_t position; std::uint32_t vendor; std::uint32_t product; std::uint32_t revision; const char* name; Dc dc; std::size_t pdo_begin; std::size_t pdo_count; std::size_t module_begin; std::size_t module_count; };
inline constexpr bool ready_for_runtime = false;
} // namespace ecmotion::generated

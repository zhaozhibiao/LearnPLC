#pragma once
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>
namespace ecmotion::runtime_config {
enum class Direction : std::uint8_t { Rx, Tx };
enum class Watchdog : std::uint8_t { Default, Enable, Disable };
struct Entry { std::uint16_t index; std::uint8_t subindex; std::uint8_t bits; std::string key; };
struct Pdo { Direction direction; std::uint8_t sync_manager; std::uint16_t index; std::size_t entry_begin; std::size_t entry_count; };
struct Dc { bool enabled{}; std::uint16_t assign_activate{}; std::uint32_t sync0_cycle_ns{}; std::int32_t sync0_shift_ns{}; std::uint32_t sync1_cycle_ns{}; };
struct SyncManager { std::uint8_t index; Direction direction; Watchdog watchdog; };
struct StartupSdo { std::uint16_t index; std::uint8_t subindex; std::vector<std::uint8_t> data; bool complete_access{}; };
struct Slave { std::uint16_t position; std::uint32_t vendor; std::uint32_t product; std::uint32_t revision; std::string name; Dc dc; std::size_t pdo_begin; std::size_t pdo_count; std::size_t module_begin; std::size_t module_count; std::vector<SyncManager> sync_managers; std::vector<StartupSdo> startup_sdos; };
struct Configuration {
    bool ready_for_runtime{};
    std::uint32_t cycle_time_ns{};
    std::size_t blocker_count{};
    std::string topology_fingerprint;
    std::string machine_sha256, io_map_sha256;
    std::vector<Entry> entries;
    std::vector<Pdo> pdos;
    std::vector<std::uint32_t> module_idents;
    std::vector<Slave> slaves;
    static Configuration parse(std::string_view text);
    static Configuration load(const std::string& path);
};
}

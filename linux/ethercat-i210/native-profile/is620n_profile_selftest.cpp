// Offline test using real configuration parsing, PDO binding, and feedback scaling.
// Link against ecmotion-io-cycle and ecmotion-axis-control; never links libethercat.
#include "axis_feedback.hpp"
#include "io_map_config.hpp"
#include "is620n_ethercat_config.hpp"
#include "pdo_io_cycle.hpp"

#include <cmath>
#include <cstdio>
#include <stdexcept>
#include <vector>

namespace cfg = ecmotion::generated;
void require(bool condition, const char* reason) {
    if (!condition) throw std::runtime_error(reason);
}

int main(int argc, char** argv) {
    try {
        require(argc == 3, "expected machine-config and io-map paths");
        auto axes = ecmotion::AxisFeedbackConfiguration::load_all(argv[1]);
        require(axes.size() == 1 && axes[0].slave_position == 0, "single axis topology");
        require(axes[0].counts_per_engineering_unit == 1048576.0, "20-bit encoder scale");
        require(axes[0].position_unit == "rev", "motor revolution engineering unit");
        require(axes[0].velocity_offset_key.find("0x60B1:0") != std::string::npos,
            "CSP velocity feedforward uses CiA 402 velocity offset");
        require(axes[0].homing.method == 35, "explicit current position zero method");
        // Keep the previously verified conservative following-error envelope until
        // a new staged motion run records the actual tracking error.
        ecmotion::MotionSafetyMonitor monitor(axes[0].safety);
        require(!monitor.update(0, 0, 0.03, false, true).overspeed_fault,
            "two observed velocity quanta must not trip the commissioning envelope");
        require(monitor.update(0, 0, axes[0].safety.maximum_actual_velocity + 0.1, false, true).overspeed_fault,
            "actual overspeed beyond the finite commissioning envelope must trip");
        std::vector<ecmotion::PdoEntryDescriptor> entries;
        std::size_t bytes = 0;
        for (const auto& pdo : cfg::pdos) {
            const auto direction = pdo.direction == cfg::Direction::Rx
                ? ecmotion::PdoDirection::ControllerOutput : ecmotion::PdoDirection::ControllerInput;
            for (std::size_t e = pdo.entry_begin; e < pdo.entry_begin + pdo.entry_count; ++e) {
                const auto& entry = cfg::entries[e];
                entries.push_back({entry.key, direction, bytes, 0, entry.bits});
                bytes += entry.bits / 8;
            }
        }
        ecmotion::PdoLayout layout(std::move(entries), bytes);
        std::vector<std::uint8_t> storage(bytes);
        ecmotion::PdoImageView image(storage, layout);
        const auto io = ecmotion::IoMapConfiguration::load(argv[2]);
        const auto bound = io.bind(layout);
        require(bound.logical_channel_count == 0, "servo-only IO is empty");
        require(io.catalog(layout, cfg::topology_fingerprint).channels.empty(), "no fake logical IO");
        ecmotion::PdoIoCycle io_cycle(bound, image);
        io_cycle.apply_safe_state();
        ecmotion::RealtimeCommand write;
        write.kind = ecmotion::RealtimeCommandKind::WriteOutputs;
        write.output_mask = 1;
        require(!io_cycle.can_apply_command(write), "absent outputs must reject writes");
        ecmotion::Cia402AxisAdapter axis(layout, axes[0]);
        image.write_from_device(layout.resolve(axes[0].position_actual_key), 104857);
        image.write_from_device(layout.resolve(axes[0].velocity_actual_key), 1048);
        image.write_from_device(layout.resolve(axes[0].statusword_key), 0x0040);
        image.write_from_device(layout.resolve(axes[0].mode_display_key), 8);
        ecmotion::RealtimeAxisSnapshot snapshot;
        axis.process(image, snapshot);
        require(std::abs(snapshot.actual_position - 104857.0 / 1048576.0) < 1e-12,
            "real PDO position scales to revolutions");
        require(std::abs(snapshot.actual_velocity - 1048.0 / 1048576.0) < 1e-12,
            "real PDO velocity scales to revolutions per second");
        require(!snapshot.homed, "startup is unhomed");
        std::puts("IS620N profile, empty IO, feedback scaling: PASS (offline; runtime remains blocked)");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "IS620N profile selftest: %s\n", error.what());
        return 1;
    }
}

#include "axis_feedback.hpp"
#include "ethercat_config.hpp"
#include "fieldbus_control_backend.hpp"
#include "fieldbus_cycle.hpp"
#include "io_map_config.hpp"
#include "pdo_io_cycle.hpp"
#include "sha256.hpp"
#include <nlohmann/json.hpp>
#include <fstream>
#include <filesystem>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <exception>
#include <stdexcept>
#include <vector>
#include <memory>
#include <limits>
#include <thread>
#include <chrono>

namespace cfg = ecmotion::generated;

class TestAxis final : public ecmotion::RealtimeAxisAdapter {
public:
    explicit TestAxis(double position = 0.0) : position_(position) {}
    bool apply_command(const ecmotion::RealtimeCommand& command) noexcept override {
        if (command.kind != ecmotion::RealtimeCommandKind::Enable) return false;
        enabled_ = true;
        return true;
    }
    bool can_start_group(double target, double minimum_position, double maximum_position,
        double velocity, double acceleration, double deceleration, double radius) const noexcept override {
        static_cast<void>(radius);
        return enabled_ && !busy_ && target >= -500.0 && target <= 500.0
            && minimum_position >= -500.0 && maximum_position <= 500.0
            && velocity >= 0.0 && acceleration >= 0.0 && deceleration >= 0.0;
    }
    double group_start_position() const noexcept override { return position_; }
    void begin_group(std::uint64_t id, double target) noexcept override {
        motion_id_ = id; target_ = target; busy_ = true; done_ = false; aborted_ = false;
    }
    void group_setpoint(double position, double velocity, bool final_point) noexcept override {
        static_cast<void>(velocity);
        position_ = position; busy_ = !final_point; done_ = final_point;
    }
    bool group_done(std::uint64_t id) const noexcept override {
        return motion_id_ == id && done_;
    }
    bool group_faulted(std::uint64_t id) const noexcept override {
        return motion_id_ != id || aborted_ || !enabled_;
    }
    void apply_safe_state() noexcept override {
        enabled_ = false; if (busy_) { busy_ = false; aborted_ = true; }
    }
    void process(ecmotion::PdoImageView&, ecmotion::RealtimeAxisSnapshot& snapshot) noexcept override {
        snapshot.actual_position = position_;
        snapshot.state = busy_ ? ecmotion::RealtimeAxisState::DiscreteMotion
            : enabled_ ? ecmotion::RealtimeAxisState::Standstill
            : ecmotion::RealtimeAxisState::Disabled;
        snapshot.motion_id = motion_id_;
        snapshot.motion_target = target_;
        snapshot.motion_busy = busy_;
        snapshot.motion_done = done_;
        snapshot.motion_aborted = aborted_;
    }
    bool enabled() const noexcept { return enabled_; }
private:
    double position_{};
    bool enabled_{};
    std::uint64_t motion_id_{};
    double target_{};
    bool busy_{};
    bool done_{};
    bool aborted_{};
};

int main(int argc, char** argv) {
    try {
        {
            auto fault_exchange = std::make_unique<ecmotion::RealtimeExchange>();
            ecmotion::RealtimeSnapshot fault_sample{};
            fault_sample.bus_ready = true;
            fault_sample.axis_count = 1;
            fault_sample.cycles = 1;
            fault_sample.sample_monotonic_ns = 1;
            fault_sample.axes[0].state = ecmotion::RealtimeAxisState::ErrorStop;
            fault_sample.axes[0].overspeed_fault = true;
            fault_exchange->publish(fault_sample);
            ecmotion::FieldbusControlBackend fault_backend(*fault_exchange, {}, {"Axis1"});
            fault_backend.set_bus_ready(true);
            if (!fault_backend.runtime_ready() || !fault_backend.io_ready()
                || fault_backend.motion_ready()
                || fault_backend.axis_snapshot("Axis1").fault.empty())
                throw std::runtime_error("axis fault incorrectly disabled runtime management/diagnostics: runtime=" + std::to_string(fault_backend.runtime_ready()) + " io=" + std::to_string(fault_backend.io_ready()) + " motion=" + std::to_string(fault_backend.motion_ready()) + " fault=" + fault_backend.axis_snapshot("Axis1").fault);
            fault_backend.set_bus_ready(false);
            if (fault_backend.runtime_ready() || fault_backend.motion_ready())
                throw std::runtime_error("unavailable bus incorrectly reported runtime ready");
        }
        if (argc != 3 && argc != 4) {
            throw std::invalid_argument(
                "usage: ecmotion-io-map-selftest <io-map.json> <reference-machine.json>");
        }
        {
            // Type-level outputs do not consume the 16 edge-action slots.
            std::vector<ecmotion::PdoEntryDescriptor> entries{{"DO", ecmotion::PdoDirection::ControllerOutput, 0, 0, 8}};
            std::vector<std::string> keys;
            for (std::size_t i=0;i<20;++i) {
                keys.push_back("REAL"+std::to_string(i));
                entries.push_back({keys.back(),ecmotion::PdoDirection::ControllerOutput,1+i*4,0,32});
            }
            ecmotion::PdoLayout bulk_layout(std::move(entries),81);
            std::vector<std::uint8_t> storage(81);
            ecmotion::PdoImageView bulk_image(storage,bulk_layout);
            ecmotion::BoundIoMap bulk_map;
            const std::vector<std::string> digital_keys{"DO"};
            bulk_map.digital_groups.emplace_back(bulk_layout,"D",ecmotion::LogicalIoDirection::Output,digital_keys,8,8,true,false);
            bulk_map.scalar_groups.emplace_back(bulk_layout,ecmotion::IoGroupDefinition{"F",ecmotion::LogicalIoDirection::Output,
                ecmotion::LogicalEncoding::Float32,20,1,keys,"unit",std::nullopt,false,-2.5});
            ecmotion::PdoIoCycle bulk_io(bulk_map,bulk_image); bulk_io.apply_safe_state();
            auto bulk_exchange=std::make_unique<ecmotion::RealtimeExchange>();
            TestAxis test_axis;
            ecmotion::FieldbusCycle bulk_cycle(*bulk_exchange,bulk_io,test_axis); bulk_cycle.set_bus_ready(true);
            ecmotion::RealtimeBatch batch;
            batch.sequence=bulk_exchange->next_command_sequence(); batch.io_output_mask=0xFF; batch.scalar_write_count=20;
            for(std::uint16_t i=0;i<20;++i) batch.scalar_writes[i]={i,0x3F800000U}; // 1.0f
            if(!bulk_exchange->try_enqueue_batch(batch)) throw std::runtime_error("bulk IO-only batch enqueue failed");
            auto sample=bulk_cycle.process(bulk_image,10);
            if(sample.digital_outputs || sample.scalar_count!=20 || sample.scalars[19].raw_bits!=0x3F800000U
                || sample.last_applied_command_sequence!=batch.sequence) throw std::runtime_error("20 REAL + 8 DO batch failed");
            auto bad=batch; bad.sequence=bulk_exchange->next_command_sequence(); bad.io_output_values=0xFF;
            bad.count=1;
            bad.commands[0]={.sequence=bad.sequence,.kind=ecmotion::RealtimeCommandKind::MoveAbsolute,.position=10,.velocity=1};
            if(!bulk_exchange->try_enqueue_batch(bad)) throw std::runtime_error("mixed axis/IO enqueue failed");
            sample=bulk_cycle.process(bulk_image,10);
            if(sample.digital_outputs || sample.scalars[0].raw_bits!=0x3F800000U || bulk_exchange->rejected_sequence()!=bad.sequence)
                throw std::runtime_error("one invalid axis plus IO partly changed outputs");
            bad=batch; bad.sequence=bulk_exchange->next_command_sequence(); bad.io_output_values=0xFF;
            bad.scalar_writes[19].raw_bits=0x7FC00000U;
            if(!bulk_exchange->try_enqueue_batch(bad)) throw std::runtime_error("invalid REAL batch enqueue failed");
            sample=bulk_cycle.process(bulk_image,11);
            if(sample.digital_outputs || sample.scalars[0].raw_bits!=0x3F800000U || bulk_exchange->rejected_sequence()!=bad.sequence)
                throw std::runtime_error("NaN REAL batch partly applied DO/REAL");
            bad=batch; bad.sequence=bulk_exchange->next_command_sequence(); bad.io_output_values=0xFF; bad.deadline_ns=11;
            if(!bulk_exchange->try_enqueue_batch(bad)) throw std::runtime_error("expired IO batch enqueue failed");
            sample=bulk_cycle.process(bulk_image,12);
            if(sample.digital_outputs || bulk_exchange->rejected_sequence()!=bad.sequence) throw std::runtime_error("expired typed IO applied");
            bad.deadline_ns=0; bad.sequence=bulk_exchange->next_command_sequence(); bulk_exchange->cancel_through(bad.sequence);
            if(!bulk_exchange->try_enqueue_batch(bad)) throw std::runtime_error("cancelled IO batch enqueue failed");
            sample=bulk_cycle.process(bulk_image,13);
            if(sample.digital_outputs || bulk_exchange->rejected_sequence()!=bad.sequence) throw std::runtime_error("cancelled typed IO applied");
            for(unsigned i=0;i<63;++i) {
                batch.sequence=bulk_exchange->next_command_sequence();
                if(!bulk_exchange->try_enqueue_batch(batch)) throw std::runtime_error("typed FIFO capacity below63");
            }
            if(bulk_exchange->try_enqueue_batch(batch)) throw std::runtime_error("typed FIFO overflow accepted");
            const auto safe_epoch=bulk_exchange->request_safe_state(); sample=bulk_cycle.process(bulk_image,14);
            if(sample.digital_outputs!=0xFF || sample.scalars[19].raw_bits!=0xC0200000U || bulk_exchange->safe_state_epoch()!=safe_epoch)
                throw std::runtime_error("Stop did not cancel queued bulk IO and restore configured nonzero safe values");
            sample=bulk_cycle.process(bulk_image,15);
            if(sample.digital_outputs!=0xFF) throw std::runtime_error("queued typed IO replayed after Stop");
            batch.scalar_write_count=513;
            if(bulk_exchange->try_enqueue_batch(batch)) throw std::runtime_error("typed IO array capacity exceeded");
        }
        for (bool batched : {false, true}) {
            auto delayed = std::make_unique<ecmotion::RealtimeExchange>();
            ecmotion::RealtimeSnapshot initial;
            initial.axis_count=1; initial.bus_ready=true; initial.cycles=1;
            delayed->publish(initial);
            ecmotion::FieldbusControlBackend backend(*delayed,{}, {"Axis1"}); backend.set_bus_ready(true);
            std::jthread publisher([&](std::stop_token token) {
                ecmotion::RealtimeBatch command;
                while(!delayed->try_dequeue_batch(command)) {
                    if(token.stop_requested()) return;
                    std::this_thread::sleep_for(std::chrono::microseconds(100));
                }
                const auto sequence=command.sequence ? command.sequence : command.commands[0].sequence;
                delayed->complete(sequence,true);
                std::this_thread::sleep_for(std::chrono::milliseconds(20)); // deterministic pre-publication window
                auto latest=initial; latest.cycles=2; latest.last_applied_command_cycle=2;
                latest.last_applied_command_sequence=sequence; latest.axes[0].motion_id=sequence;
                delayed->publish(latest);
            });
            const auto id=batched ? backend.scan_batch({{.kind=ecmotion::RealtimeCommandKind::MoveAbsolute,
                .position=10,.velocity=1}}, {"Axis1"},{},0) : backend.move_absolute("Axis1",10,1);
            if(backend.axis_snapshot("Axis1").motion_id!=id || backend.applied_command_stamp().second!=2)
                throw std::runtime_error("successful receipt returned before motion feedback publication");
        }
        std::vector<ecmotion::PdoDirection> directions(
            cfg::entries.size(), ecmotion::PdoDirection::ControllerInput);
        for (const auto& pdo : cfg::pdos) {
            for (std::size_t index = 0; index < pdo.entry_count; ++index) {
                directions[pdo.entry_begin + index] = pdo.direction == cfg::Direction::Rx
                    ? ecmotion::PdoDirection::ControllerOutput
                    : ecmotion::PdoDirection::ControllerInput;
            }
        }
        std::vector<ecmotion::PdoEntryDescriptor> descriptors;
        std::size_t offset = 0U;
        for (std::size_t index = 0; index < cfg::entries.size(); ++index) {
            const auto& entry = cfg::entries[index];
            descriptors.push_back({entry.key, directions[index], offset, 0U, entry.bits});
            offset += (entry.bits + 7U) / 8U;
        }
        ecmotion::PdoLayout layout(std::move(descriptors), offset);
        const auto configuration = ecmotion::IoMapConfiguration::load(argv[1]);
        if (ecmotion::sha256("") != "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            || ecmotion::sha256("abc") != "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
            || ecmotion::sha256("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")
                != "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")
            throw std::runtime_error("SHA256 reference vectors failed");
        if (argc == 4) {
            std::ifstream fingerprint_file(argv[3]);
            const auto expected = nlohmann::json::parse(fingerprint_file);
            if (configuration.signature() != expected.at("signature").get<std::string>()
                || configuration.fingerprint(expected.at("topologyFingerprint").get<std::string>())
                    != expected.at("fingerprint").get<std::string>())
                throw std::runtime_error("Windows/Linux hardware fingerprints differ");
        }
        const auto bound = configuration.bind(layout);
        if (configuration.groups().size() != 7U || bound.logical_channel_count != 56U
            || bound.digital_groups.size() != 2U || bound.scalar_groups.size() != 5U) {
            throw std::runtime_error("project IO map did not bind to the expected logical shape");
        }
        std::vector<std::uint8_t> bytes(layout.image_size_bytes());
        ecmotion::PdoImageView image(bytes, layout);
        const auto& ai_definition = configuration.groups().at(2);
        image.write_from_device(layout.resolve(ai_definition.source_entries.front()), 100U);
        std::vector<ecmotion::ScalarSample> samples(bound.scalar_groups.front().channel_count());
        bound.scalar_groups.front().read(image, samples);
        if (samples.front().value != 100.0
            || samples.front().quality != ecmotion::ChannelQuality::CommissioningRequired) {
            throw std::runtime_error("analog commissioning quality was not preserved");
        }
        const auto& analog_output = bound.scalar_groups.at(1);
        bool commissioning_blocked = false;
        try {
            std::vector<double> outputs(analog_output.channel_count(), 1.0);
            analog_output.write(image, outputs);
        } catch (const std::logic_error&) {
            commissioning_blocked = true;
        }
        if (!commissioning_blocked) throw std::runtime_error("uncommissioned analog output was accepted");
        analog_output.write_safe(image);
        for (const auto& key : configuration.groups().at(3).source_entries) {
            if (image.read_signed(layout.resolve(key)) != 0) {
                throw std::runtime_error("analog safe output was not written");
            }
        }

        ecmotion::RealtimeExchange exchange;
        ecmotion::PdoIoCycle cycle(bound, image);
        if (cycle.digital_input_count() != 16U || cycle.digital_output_count() != 16U) {
            throw std::runtime_error("digital IO cycle did not expose the configured channel counts");
        }
        const auto& digital_input = configuration.groups().at(0);
        image.write_from_device(layout.resolve(digital_input.source_entries.at(0)), 0xA5U);
        image.write_from_device(layout.resolve(digital_input.source_entries.at(1)), 0x01U);
        ecmotion::RealtimeCommand output_command{
            .sequence = exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::WriteOutputs,
            .output_mask = (std::uint64_t{1} << 0U) | (std::uint64_t{1} << 9U),
            .output_values = (std::uint64_t{1} << 0U) | (std::uint64_t{1} << 9U)};
        const auto axis_configuration = ecmotion::AxisFeedbackConfiguration::load(argv[2], "Axis1");
        auto current_position_configuration = axis_configuration;
        auto position_stability_configuration = axis_configuration;
        {
            std::ifstream input(argv[2]);
            auto machine = nlohmann::json::parse(input);
            auto& axis = machine.at("axes").at(0);
            axis["homing"] = {{"required", true}, {"method", 35},
                {"searchSpeedRaw", 100}, {"latchSpeedRaw", 10},
                {"accelerationRaw", 100}, {"offsetRaw", 0},
                {"timeoutMilliseconds", 1000}};
            struct TemporaryMachine {
                std::filesystem::path path;
                ~TemporaryMachine() { std::error_code ignored; std::filesystem::remove(path, ignored); }
            } temporary{std::filesystem::temp_directory_path() /
                ("ecmotion-home35-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + ".json")};
            const auto load = [&]() {
                { std::ofstream output(temporary.path); output << machine.dump(); }
                return ecmotion::AxisFeedbackConfiguration::load(temporary.path.string(), axis.at("name"));
            };
            const auto require_rejected = [&]() {
                bool rejected = false;
                try { static_cast<void>(load()); } catch (const std::invalid_argument&) { rejected = true; }
                if (!rejected) throw std::runtime_error("current-position homing accepted without narrow permission");
            };
            require_rejected(); // Missing permission defaults to denied.
            axis["homing"]["allowCurrentPositionZero"] = false;
            require_rejected();
            axis["homing"]["allowCurrentPositionZero"] = true;
            current_position_configuration = load();
            if (current_position_configuration.homing.method != 35)
                throw std::runtime_error("explicit method-35 permission was not preserved");
            axis["homing"]["method"] = 37;
            require_rejected(); // Permission never extends to method 37.
            axis["homing"]["method"] = 0;
            axis["motionCompletion"] = {{"positionTolerance", 0.0001},
                {"velocityTolerance", 0.0005}, {"dwellMilliseconds", 100},
                {"positionStabilityCompletion", true}};
            position_stability_configuration = load();
        }
        for (int scenario = 0; scenario < 6; ++scenario) {
            // Real PDO feedback with 20-bit-encoder-sized jitter and quantized
            // velocity. Scenario 0 explicitly enables bounded-position completion;
            // scenario 1 omits the option and retains the legacy velocity gate.
            auto completion_configuration = scenario == 1 ? axis_configuration : position_stability_configuration;
            completion_configuration.commissioning_required = false;
            completion_configuration.homing.required = false;
            completion_configuration.counts_per_engineering_unit = 1'000'000.0;
            completion_configuration.direction = 1;
            completion_configuration.completion_position_tolerance = 0.0001;
            completion_configuration.completion_velocity_tolerance = 0.0005;
            completion_configuration.completion_dwell_seconds = 0.1;
            completion_configuration.safety.maximum_actual_velocity = 0.05;
            completion_configuration.safety.maximum_following_error = 0.005;
            completion_configuration.safety.following_error_timeout_seconds = 0.02;
            ecmotion::Cia402AxisAdapter completion_axis(layout, completion_configuration);
            const auto position_key = layout.resolve(axis_configuration.position_actual_key);
            const auto velocity_key = layout.resolve(axis_configuration.velocity_actual_key);
            image.write_from_device(layout.resolve(axis_configuration.statusword_key), 0x0027U);
            image.write_from_device(layout.resolve(axis_configuration.mode_display_key), 8U);
            image.write_from_device(layout.resolve(axis_configuration.error_code_key), 0U);
            image.write_from_device(position_key, 0U);
            image.write_from_device(velocity_key, 0U);
            ecmotion::RealtimeAxisSnapshot completion_snapshot;
            completion_axis.process(image, completion_snapshot);
            if (!completion_axis.apply_command({.kind = ecmotion::RealtimeCommandKind::Enable}))
                throw std::runtime_error("completion fixture enable failed");
            if (!completion_axis.apply_command({.sequence = 91,
                .kind = ecmotion::RealtimeCommandKind::MoveAbsolute,
                .position = scenario == 5 ? 0.00005 : 0.0, .velocity = scenario == 5 ? 0.0000001 : 0.01}))
                throw std::runtime_error("completion fixture move failed");
            for (int tick = 0; tick < 180; ++tick) {
                const auto raw_position = scenario == 2 ? tick % 80
                    : scenario == 3 ? 200 : tick % 2 == 0 ? -7 : 7;
                const auto raw_velocity = scenario == 4 ? 60'000 : tick % 2 == 0 ? -15'000 : 15'000;
                image.write_from_device(position_key, static_cast<std::uint32_t>(raw_position));
                image.write_from_device(velocity_key, static_cast<std::uint32_t>(raw_velocity));
                completion_axis.process(image, completion_snapshot);
                if (std::abs(completion_snapshot.actual_velocity - static_cast<double>(raw_velocity) / 1'000'000.0) > 1e-12)
                    throw std::runtime_error("position stability completion altered raw velocity feedback");
                if (scenario == 0 && tick < 99 && completion_snapshot.motion_done)
                    throw std::runtime_error("position stability completion bypassed dwell");
            }
            if (scenario == 0 && (!completion_snapshot.motion_done || completion_snapshot.motion_busy
                || completion_snapshot.motion_aborted))
                throw std::runtime_error("bounded position jitter with quantized velocity never completed");
            if (scenario != 0 && completion_snapshot.motion_done)
                throw std::runtime_error("legacy velocity, position drift, target tolerance or overspeed gate bypassed");
            if (scenario == 4 && (!completion_snapshot.overspeed_fault || !completion_snapshot.motion_aborted))
                throw std::runtime_error("position stability completion bypassed raw velocity overspeed protection");
        }
        {
            auto freewheel_configuration = axis_configuration;
            freewheel_configuration.commissioning_required = false;
            freewheel_configuration.homing.required = false;
            freewheel_configuration.counts_per_engineering_unit = 1'000'000.0;
            freewheel_configuration.direction = 1;
            freewheel_configuration.safety.maximum_actual_velocity = 0.05;
            ecmotion::Cia402AxisAdapter freewheel_axis(layout, freewheel_configuration);
            const auto status = layout.resolve(axis_configuration.statusword_key);
            const auto velocity = layout.resolve(axis_configuration.velocity_actual_key);
            image.write_from_device(status, 0x0040U); // Actual drive disabled; shaft can detent freely.
            image.write_from_device(layout.resolve(axis_configuration.mode_display_key), 8U);
            image.write_from_device(layout.resolve(axis_configuration.position_actual_key), 0U);
            image.write_from_device(layout.resolve(axis_configuration.error_code_key), 0U);
            image.write_from_device(velocity, 60'000U);
            ecmotion::RealtimeAxisSnapshot feedback;
            freewheel_axis.process(image, feedback);
            if (feedback.overspeed_fault || feedback.state != ecmotion::RealtimeAxisState::Disabled
                || std::abs(feedback.actual_velocity - 0.06) > 1e-12)
                throw std::runtime_error("disabled freewheel latched controlled-motion overspeed or hid feedback");
            const ecmotion::RealtimeCommand request_enable{.kind = ecmotion::RealtimeCommandKind::Enable};
            if (freewheel_axis.can_apply_command(request_enable) || freewheel_axis.apply_command(request_enable))
                throw std::runtime_error("Enable accepted a fast freewheeling rotor");
            image.write_from_device(velocity, 0U);
            freewheel_axis.process(image, feedback);
            if (!freewheel_axis.can_apply_command(request_enable) || !freewheel_axis.apply_command(request_enable))
                throw std::runtime_error("stationary disabled rotor could not enable");
            image.write_from_device(status, 0x0027U);
            freewheel_axis.process(image, feedback);
            if (!freewheel_axis.apply_command({.sequence = 92,
                .kind = ecmotion::RealtimeCommandKind::MoveAbsolute, .position = 0.001, .velocity = 0.01}))
                throw std::runtime_error("freewheel fixture move rejected");
            image.write_from_device(velocity, 60'000U);
            freewheel_axis.process(image, feedback);
            if (!feedback.overspeed_fault || !feedback.motion_aborted || feedback.motion_done)
                throw std::runtime_error("enabled-drive overspeed did not latch and abort");
            image.write_from_device(status, 0x0040U);
            freewheel_axis.process(image, feedback);
            if (!feedback.overspeed_fault || freewheel_axis.can_apply_command(request_enable))
                throw std::runtime_error("disabling silently cleared a latched overspeed fault");
            if (!freewheel_axis.apply_command({.kind = ecmotion::RealtimeCommandKind::Reset}))
                throw std::runtime_error("disabled healthy monitoring state could not explicitly reset");
            if (freewheel_axis.can_apply_command(request_enable) || freewheel_axis.apply_command(request_enable))
                throw std::runtime_error("Reset bypassed fast-rotor Enable rejection");
            image.write_from_device(velocity, 0U);
            freewheel_axis.process(image, feedback);
            if (feedback.overspeed_fault || !freewheel_axis.apply_command(request_enable))
                throw std::runtime_error("explicit reset failed to recover a stationary rotor");
            ecmotion::MotionSafetyMonitor generic(freewheel_configuration.safety);
            if (!generic.update(0, 0, 0.06, false, false).overspeed_fault)
                throw std::runtime_error("default generic velocity monitoring was weakened");
            ecmotion::MotionSafetyMonitor invalid(freewheel_configuration.safety);
            if (!invalid.update(0, 0, std::numeric_limits<double>::quiet_NaN(), false, false, false).overspeed_fault)
                throw std::runtime_error("nonfinite velocity escaped generic monitoring");
            image.write_from_device(status, 0x0027U);
            image.write_from_device(velocity, 60'000U);
            ecmotion::Cia402AxisAdapter externally_enabled_axis(layout, freewheel_configuration);
            externally_enabled_axis.process(image, feedback); // No controller Enable request.
            if (!feedback.overspeed_fault)
                throw std::runtime_error("actual enabled drive escaped overspeed without a controller Enable request");
            image.write_from_device(status, 0x0007U); // Quick Stop Active still has operation-enabled bit 2.
            ecmotion::Cia402AxisAdapter quick_stop_axis(layout, freewheel_configuration);
            quick_stop_axis.process(image, feedback);
            if (!feedback.overspeed_fault)
                throw std::runtime_error("Quick Stop Active escaped actual velocity overspeed protection");
            if (quick_stop_axis.can_apply_command(request_enable))
                throw std::runtime_error("Quick Stop Active accepted a motion enable request");
            image.write_from_device(velocity, 0U);
        }
        for (const auto forbidden_method : {35, 37}) {
            auto forbidden = axis_configuration;
            forbidden.homing.method = static_cast<std::int8_t>(forbidden_method);
            bool rejected = false;
            try { ecmotion::Cia402AxisAdapter unpermitted(layout, forbidden); }
            catch (const std::invalid_argument&) { rejected = true; }
            if (!rejected) throw std::runtime_error("programmatic homing configuration bypassed permission");
        }
        ecmotion::Cia402AxisAdapter axis_adapter(layout, axis_configuration);
        ecmotion::FieldbusCycle fieldbus_cycle(exchange, cycle, axis_adapter);
        fieldbus_cycle.set_bus_ready(true);
        if (!exchange.try_enqueue(output_command)) throw std::runtime_error("digital output command was not queued");
        auto cycle_snapshot = fieldbus_cycle.process(image);
        if (cycle_snapshot.digital_inputs != 0x1A5U
            || cycle_snapshot.digital_outputs != output_command.output_values
            || exchange.completed_sequence() != output_command.sequence) {
            throw std::runtime_error("digital IO cycle did not exchange PDO values");
        }
        const auto typed_snapshot = exchange.snapshot();
        if (typed_snapshot.scalar_count != 24U || typed_snapshot.scalars[0].engineering_value != 100.0
            || typed_snapshot.scalars[0].raw_bits != 100U || typed_snapshot.scalars[0].quality != 1U
            || typed_snapshot.scalars[0].encoding != 1U || typed_snapshot.scalars[0].output
            || !typed_snapshot.scalars[8].output)
            throw std::runtime_error("typed scalar snapshot lost raw, engineering, quality or direction");
        ecmotion::RealtimeSnapshot reused_snapshot;
        reused_snapshot.digital_inputs = reused_snapshot.digital_outputs = ~std::uint64_t{0};
        cycle.sample(reused_snapshot);
        if (reused_snapshot.digital_inputs != 0x1A5U
            || reused_snapshot.digital_outputs != output_command.output_values)
            throw std::runtime_error("reused IO snapshot retained stale digital bits");
        std::vector<std::string> scalar_names;
        for (const auto& group : configuration.groups())
            if (group.encoding != ecmotion::LogicalEncoding::PackedBoolean)
                for (std::size_t i = 0; i < group.channel_count; ++i)
                    scalar_names.push_back(group.name_prefix + "." + std::to_string(i));
        ecmotion::FieldbusControlBackend typed_backend(exchange, {}, {"Axis1"}, {}, {}, scalar_names);
        const auto typed_channels = typed_backend.scalar_io();
        if (typed_channels.size() != 24U || typed_channels[0].name != "MainIo.AI.0"
            || typed_channels[0].quality != 1U || typed_channels[8].name != "MainIo.AO.0")
            throw std::runtime_error("typed backend catalog does not preserve scalar identity");
        image.write_from_device(layout.resolve(ai_definition.source_entries.front()), 0xFF85U);
        const auto tc_key = configuration.groups().at(4).source_entries.front();
        image.write_from_device(layout.resolve(tc_key), 0x7FC00000U);
        cycle.sample(reused_snapshot);
        exchange.publish(reused_snapshot);
        const auto invalid_channels = typed_backend.scalar_io();
        if (invalid_channels[0].engineering_value != -123.0 || invalid_channels[0].raw_bits != 0xFF85U
            || invalid_channels[12].quality != 2U || invalid_channels[12].raw_bits != 0x7FC00000U)
            throw std::runtime_error("signed INT or nonfinite REAL lost its raw value/quality");
        {
            ecmotion::BoundIoMap commissioned;
            commissioned.digital_groups = bound.digital_groups;
            for (auto definition : configuration.groups())
                if (definition.encoding != ecmotion::LogicalEncoding::PackedBoolean) {
                    definition.commissioning_required = false;
                    commissioned.scalar_groups.emplace_back(layout, definition);
                }
            std::vector<std::uint8_t> writable_bytes(layout.image_size_bytes());
            ecmotion::PdoImageView writable_image(writable_bytes, layout);
            ecmotion::PdoIoCycle writable_io(commissioned, writable_image);
            ecmotion::RealtimeExchange writable_exchange;
            TestAxis writable_axis;
            ecmotion::FieldbusCycle writable_fieldbus(writable_exchange, writable_io, writable_axis);
            writable_fieldbus.set_bus_ready(true);
            const auto ao_handle = layout.resolve(configuration.groups().at(3).source_entries[0]);
            ecmotion::RealtimeBatch writes;
            writes.count = 2;
            auto sequence = writable_exchange.next_command_sequence();
            writes.commands[0] = {.sequence = sequence, .kind = ecmotion::RealtimeCommandKind::WriteOutputs,
                .output_mask = 1, .output_values = 1};
            writes.commands[1] = {.sequence = sequence, .kind = ecmotion::RealtimeCommandKind::WriteScalarOutput,
                .scalar_index = 8, .scalar_raw_bits = 0xFF85U};
            if (!writable_exchange.try_enqueue_batch(writes)) throw std::runtime_error("typed batch not queued");
            auto written = writable_fieldbus.process(writable_image);
            if (written.digital_outputs != 1 || writable_image.read_signed(ao_handle) != -123
                || writable_exchange.completed_sequence() != sequence || written.scalars[8].quality != 0)
                throw std::runtime_error("mixed DO/INT output batch not applied in one cycle");
            auto bad = writes;
            sequence = writable_exchange.next_command_sequence();
            bad.commands[0].sequence = bad.commands[1].sequence = sequence;
            bad.commands[0].output_mask = bad.commands[0].output_values = 2;
            bad.commands[1].scalar_index = 12; // input cannot be written
            if (!writable_exchange.try_enqueue_batch(bad)) throw std::runtime_error("bad typed batch not queued");
            written = writable_fieldbus.process(writable_image);
            if (written.digital_outputs != 1 || writable_image.read_signed(ao_handle) != -123
                || writable_exchange.rejected_sequence() != sequence)
                throw std::runtime_error("bad scalar caused a partial digital write");
            bad = writes; bad.commands[0] = bad.commands[1]; // same scalar twice
            sequence = writable_exchange.next_command_sequence();
            bad.commands[0].sequence = bad.commands[1].sequence = sequence;
            if (!writable_exchange.try_enqueue_batch(bad)) throw std::runtime_error("duplicate typed batch not queued");
            static_cast<void>(writable_fieldbus.process(writable_image));
            if (writable_exchange.rejected_sequence() != sequence
                || writable_io.can_apply_command({.kind = ecmotion::RealtimeCommandKind::WriteScalarOutput,
                    .scalar_index = 8, .scalar_raw_bits = 0x10000U})
                || cycle.can_apply_command(writes.commands[1]))
                throw std::runtime_error("scalar duplicate/width/commissioning gate not enforced");
            static_cast<void>(writable_exchange.request_safe_state());
            written = writable_fieldbus.process(writable_image);
            if (written.digital_outputs != 0 || writable_image.read_signed(ao_handle) != 0)
                throw std::runtime_error("typed output safe state not restored");
        }
        ecmotion::RealtimeCommand unsupported{
            .sequence = exchange.next_command_sequence(), .kind = ecmotion::RealtimeCommandKind::Enable};
        if (!exchange.try_enqueue(unsupported)) throw std::runtime_error("unsupported command was not queued");
        cycle_snapshot = fieldbus_cycle.process(image);
        if (exchange.rejected_sequence() != unsupported.sequence) {
            throw std::runtime_error("IO-only cycle accepted an axis command");
        }
        const auto safe_epoch = exchange.request_safe_state();
        cycle_snapshot = fieldbus_cycle.process(image);
        if (exchange.snapshot().digital_outputs != 0U || exchange.safe_state_epoch() < safe_epoch) {
            throw std::runtime_error("safe-state request did not clear digital outputs");
        }

        image.write_from_device(layout.resolve(axis_configuration.statusword_key), 0x0027U);
        image.write_from_device(layout.resolve(axis_configuration.position_actual_key), 8'388'608U);
        image.write_from_device(layout.resolve(axis_configuration.velocity_actual_key), 838'861U);
        image.write_from_device(layout.resolve(axis_configuration.mode_display_key), 8U);
        image.write_from_device(layout.resolve(axis_configuration.error_code_key), 0U);
        auto axis_snapshot = fieldbus_cycle.process(image);
        if (std::abs(axis_snapshot.actual_position - 10.0) > 1e-9
            || std::abs(axis_snapshot.actual_velocity - 1.000000238418579) > 1e-9
            || axis_snapshot.statusword != 0x0027U || axis_snapshot.mode_display != 8
            || axis_snapshot.error_code != 0U || !axis_snapshot.commissioning_required
            || axis_snapshot.state != ecmotion::RealtimeAxisState::Standstill) {
            throw std::runtime_error("CiA 402 PDO feedback was not converted into the realtime snapshot");
        }
        if (image.read_unsigned(layout.resolve(axis_configuration.controlword_key)) != 0x0006U
            || image.read_signed(layout.resolve(axis_configuration.target_position_key)) != 8'388'608
            || image.read_signed(layout.resolve(axis_configuration.velocity_offset_key)) != 0) {
            throw std::runtime_error("CiA 402 safe hold outputs were not generated");
        }
        ecmotion::RealtimeCommand blocked_enable{
            .sequence = exchange.next_command_sequence(), .kind = ecmotion::RealtimeCommandKind::Enable};
        if (!exchange.try_enqueue(blocked_enable)) throw std::runtime_error("enable was not queued");
        static_cast<void>(fieldbus_cycle.process(image));
        if (exchange.rejected_sequence() != blocked_enable.sequence) {
            throw std::runtime_error("uncommissioned generic axis accepted enable");
        }

        auto commissioned_configuration = axis_configuration;
        commissioned_configuration.name = "VendorNeutralCiA402Axis";
        commissioned_configuration.commissioning_required = false;
        // Legacy CSP trajectory fixture is intentionally pre-referenced in memory;
        // the independent homing fixture below exercises the real interlock.
        commissioned_configuration.homing.required = false;
        ecmotion::RealtimeExchange commissioned_exchange;
        ecmotion::PdoIoCycle commissioned_io(bound, image);
        ecmotion::Cia402AxisAdapter commissioned_axis(layout, commissioned_configuration);
        ecmotion::FieldbusCycle commissioned_cycle(
            commissioned_exchange, commissioned_io, commissioned_axis);
        commissioned_cycle.set_bus_ready(true);
        // Prime feedback and software-limit validity before accepting any enable command.
        static_cast<void>(commissioned_cycle.process(image));
        ecmotion::RealtimeCommand enable{
            .sequence = commissioned_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Enable};
        if (!commissioned_exchange.try_enqueue(enable)) throw std::runtime_error("enable was not queued");
        const auto statusword = layout.resolve(axis_configuration.statusword_key);
        const auto controlword = layout.resolve(axis_configuration.controlword_key);
        image.write_from_device(statusword, 0x0040U);
        static_cast<void>(commissioned_cycle.process(image));
        if (image.read_unsigned(controlword) != 0x0006U) throw std::runtime_error("CiA 402 shutdown failed");
        image.write_from_device(statusword, 0x0021U);
        static_cast<void>(commissioned_cycle.process(image));
        if (image.read_unsigned(controlword) != 0x0007U) throw std::runtime_error("CiA 402 switch-on failed");
        image.write_from_device(statusword, 0x0023U);
        static_cast<void>(commissioned_cycle.process(image));
        if (image.read_unsigned(controlword) != 0x000FU) throw std::runtime_error("CiA 402 enable failed");
        ecmotion::RealtimeCommand reset{
            .sequence = commissioned_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Reset};
        if (!commissioned_exchange.try_enqueue(reset)) throw std::runtime_error("reset was not queued");
        image.write_from_device(statusword, 0x0008U);
        static_cast<void>(commissioned_cycle.process(image));
        if (image.read_unsigned(controlword) != 0x0080U) throw std::runtime_error("CiA 402 reset pulse failed");
        static_cast<void>(commissioned_cycle.process(image));
        if (image.read_unsigned(controlword) != 0U) throw std::runtime_error("CiA 402 reset was not one-shot");

        // Reset acknowledgment must be observed while disabled before an
        // independent Enable may start the state machine again.
        image.write_from_device(statusword, 0x0040U);
        static_cast<void>(commissioned_cycle.process(image));
        enable.sequence = commissioned_exchange.next_command_sequence();
        if (!commissioned_exchange.try_enqueue(enable)) throw std::runtime_error("second enable was not queued");
        static_cast<void>(commissioned_cycle.process(image));
        image.write_from_device(statusword, 0x0027U);
        static_cast<void>(commissioned_cycle.process(image));
        if (!commissioned_axis.can_start_group(11.0, 10.0, 11.0, 10.0, 50.0, 50.0, 0.0)
            || commissioned_axis.can_start_group(501.0, 10.0, 501.0, 10.0, 50.0, 50.0, 0.0)
            || commissioned_axis.can_start_group(11.0, 10.0, 11.0, 1000.0, 50.0, 50.0, 0.0))
            throw std::runtime_error("generic CiA 402 group readiness did not enforce axis limits");
        ecmotion::RealtimeCommand invalid_move{
            .sequence = commissioned_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::MoveAbsolute,
            .position = 501.0,
            .velocity = 10.0};
        if (!commissioned_exchange.try_enqueue(invalid_move)) throw std::runtime_error("invalid move was not queued");
        static_cast<void>(commissioned_cycle.process(image));
        if (commissioned_exchange.rejected_sequence() != invalid_move.sequence) {
            throw std::runtime_error("soft-limit violation was accepted");
        }
        ecmotion::RealtimeCommand move{
            .sequence = commissioned_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::MoveAbsolute,
            .position = 11.0,
            .velocity = 10.0};
        if (!commissioned_exchange.try_enqueue(move)) throw std::runtime_error("move was not queued");
        const auto published_target_before_move = static_cast<double>(image.read_signed(layout.resolve(axis_configuration.target_position_key)))
            / commissioned_configuration.counts_per_engineering_unit * commissioned_configuration.direction;
        const auto moving_snapshot = commissioned_cycle.process(image);
        if (std::abs(moving_snapshot.axes[0].cycle_target-published_target_before_move)>1e-12
            || moving_snapshot.axes[0].peak_error < std::abs(moving_snapshot.axes[0].cycle_error))
            throw std::runtime_error("per-cycle tracking diagnostics lost generated position error");
        if (moving_snapshot.last_applied_command_sequence != move.sequence
            || moving_snapshot.last_applied_command_cycle != moving_snapshot.cycles)
            throw std::runtime_error("Accepted command was not stamped with its PDO cycle");
        const auto raw_move_target = image.read_signed(
            layout.resolve(axis_configuration.target_position_key));
        if (moving_snapshot.state != ecmotion::RealtimeAxisState::DiscreteMotion
            || raw_move_target <= 8'388'608 || !moving_snapshot.axes[0].motion_busy
            || moving_snapshot.axes[0].motion_done
            || moving_snapshot.axes[0].motion_id != move.sequence) {
            throw std::runtime_error("generic absolute trajectory was not written to CiA 402 PDO");
        }
        if (image.read_signed(layout.resolve(axis_configuration.velocity_offset_key)) <= 0)
            throw std::runtime_error("CSP trajectory velocity was not written as velocity feedforward");
        bool reached = false;
        for (int cycle_index = 0; cycle_index < 2000; ++cycle_index) {
            const auto previous_target = image.read_unsigned(
                layout.resolve(axis_configuration.target_position_key));
            const auto previous_velocity = image.read_unsigned(
                layout.resolve(axis_configuration.velocity_offset_key));
            image.write_from_device(layout.resolve(axis_configuration.position_actual_key), previous_target);
            image.write_from_device(layout.resolve(axis_configuration.velocity_actual_key), previous_velocity);
            const auto feedback = commissioned_cycle.process(image);
            if (feedback.axes[0].motion_done) {
                if (cycle_index < 20 || feedback.axes[0].motion_busy
                    || feedback.axes[0].motion_aborted
                    || std::abs(feedback.axes[0].actual_position - 11.0) > 0.02) {
                    throw std::runtime_error("motion completion ignored actual feedback or dwell");
                }
                reached = true;
                break;
            }
        }
        if (!reached) throw std::runtime_error("motion completion was never reported");
        ecmotion::RealtimeCommand interrupted_move{
            .sequence = commissioned_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::MoveAbsolute,
            .position = 12.0, .velocity = 10.0};
        if (!commissioned_exchange.try_enqueue(interrupted_move))
            throw std::runtime_error("interrupted move was not queued");
        const auto interrupted_snapshot = commissioned_cycle.process(image);
        if (!interrupted_snapshot.axes[0].motion_busy
            || interrupted_snapshot.axes[0].motion_done
            || interrupted_snapshot.axes[0].motion_id != interrupted_move.sequence)
            throw std::runtime_error("new motion did not replace historical completion");
        ecmotion::RealtimeCommand stop{
            .sequence = commissioned_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Stop};
        if (!commissioned_exchange.try_enqueue(stop)) throw std::runtime_error("stop was not queued");
        const auto stopping_snapshot = commissioned_cycle.process(image);
        if (stopping_snapshot.state != ecmotion::RealtimeAxisState::Standstill
            && stopping_snapshot.state != ecmotion::RealtimeAxisState::Stopping) {
            throw std::runtime_error("generic controlled stop was not applied");
        }
        if (!stopping_snapshot.axes[0].motion_aborted || stopping_snapshot.axes[0].motion_done)
            throw std::runtime_error("stopped motion was not reported as aborted");
        commissioned_cycle.set_bus_ready(false);
        const auto bus_fault_snapshot = commissioned_cycle.process(image);
        if (bus_fault_snapshot.bus_ready || image.read_unsigned(controlword) == 0x000FU) {
            throw std::runtime_error("incomplete WKC did not force the realtime cycle safe");
        }
        enable.sequence = commissioned_exchange.next_command_sequence();
        if (!commissioned_exchange.try_enqueue(enable)) throw std::runtime_error("bus-fault enable was not queued");
        static_cast<void>(commissioned_cycle.process(image));
        if (commissioned_exchange.rejected_sequence() != enable.sequence) {
            throw std::runtime_error("realtime cycle accepted a command while the bus was not ready");
        }
        commissioned_cycle.set_bus_ready(true);

        for (auto homing_configuration : {axis_configuration, current_position_configuration}) {
        homing_configuration.name = "GenericHomingAxis";
        homing_configuration.commissioning_required = false;
        if (homing_configuration.homing.method != 35) homing_configuration.homing.method = 1;
        homing_configuration.homing.timeout_seconds = 1.0;
        ecmotion::RealtimeExchange homing_exchange;
        ecmotion::PdoIoCycle homing_io(bound, image);
        ecmotion::Cia402AxisAdapter homing_axis(layout, homing_configuration);
        ecmotion::FieldbusCycle homing_cycle(homing_exchange, homing_io, homing_axis);
        homing_cycle.set_bus_ready(true);
        const auto mode_display = layout.resolve(axis_configuration.mode_display_key);
        const auto mode_command = layout.resolve(axis_configuration.mode_of_operation_key);
        image.write_from_device(statusword, 0x0027U);
        image.write_from_device(mode_display, 8U);
        image.write_from_device(layout.resolve(axis_configuration.velocity_actual_key), 0U);
        static_cast<void>(homing_cycle.process(image));
        ecmotion::RealtimeCommand home_enable{
            .sequence = homing_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Enable};
        if (!homing_exchange.try_enqueue(home_enable)) throw std::runtime_error("homing enable queue failed");
        static_cast<void>(homing_cycle.process(image));
        ecmotion::RealtimeCommand before_home{
            .sequence = homing_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::MoveAbsolute,
            .position = 11.0, .velocity = 10.0};
        if (!homing_exchange.try_enqueue(before_home)) throw std::runtime_error("unhomed move queue failed");
        static_cast<void>(homing_cycle.process(image));
        if (homing_exchange.rejected_sequence() != before_home.sequence
            || homing_axis.can_start_group(11.0, 10.0, 11.0, 10.0, 50.0, 50.0, 0.0))
            throw std::runtime_error("unhomed real axis accepted motion");
        ecmotion::RealtimeCommand home{
            .sequence = homing_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Home};
        if (!homing_exchange.try_enqueue(home)) throw std::runtime_error("home queue failed");
        auto home_snapshot = homing_cycle.process(image);
        if (homing_exchange.rejected_sequence() == home.sequence
            || home_snapshot.axes[0].homed
            || home_snapshot.axes[0].state != ecmotion::RealtimeAxisState::Homing
            || image.read_signed(mode_command) != 6)
            throw std::runtime_error("homing request did not enter HM mode");
        image.write_from_device(mode_display, 6U);
        home_snapshot = homing_cycle.process(image);
        if (image.read_unsigned(controlword) != 0x001FU || home_snapshot.axes[0].homed)
            throw std::runtime_error("homing start was not a guarded mode-6 command");
        // The drive must report an unattained state after the start edge; a stale
        // attained bit from a previous reference is not a new homing success.
        static_cast<void>(homing_cycle.process(image));
        image.write_from_device(statusword, 0x1427U);
        // Attained outside HM mode must never count as proof, even after an
        // unattained sample. Return to mode 6 before accepting attainment.
        image.write_from_device(mode_display, 8U);
        home_snapshot = homing_cycle.process(image);
        if (home_snapshot.axes[0].homed || image.read_signed(mode_command) != 6)
            throw std::runtime_error("homing accepted attained feedback outside mode 6");
        image.write_from_device(mode_display, 6U);
        home_snapshot = homing_cycle.process(image);
        if (home_snapshot.axes[0].homed || image.read_signed(mode_command) != 8)
            throw std::runtime_error("homing attained was reported before CSP restored: homed="
                + std::to_string(home_snapshot.axes[0].homed)
                + " mode=" + std::to_string(image.read_signed(mode_command)));
        image.write_from_device(mode_display, 8U);
        image.write_from_device(layout.resolve(axis_configuration.velocity_actual_key),
            static_cast<std::uint32_t>(axis_configuration.counts_per_engineering_unit));
        home_snapshot = homing_cycle.process(image);
        if (home_snapshot.axes[0].homed)
            throw std::runtime_error("homing accepted CSP restoration before velocity settled");
        image.write_from_device(layout.resolve(axis_configuration.velocity_actual_key), 0U);
        home_snapshot = homing_cycle.process(image);
        if (!home_snapshot.axes[0].homed
            || home_snapshot.axes[0].state != ecmotion::RealtimeAxisState::Standstill
            || !homing_axis.can_start_group(11.0, 10.0, 11.0, 10.0, 50.0, 50.0, 0.0))
            throw std::runtime_error("verified homing did not unlock CSP motion");
        const auto homing_safe_epoch = homing_exchange.request_safe_state();
        home_snapshot = homing_cycle.process(image);
        if (home_snapshot.axes[0].homed || homing_exchange.safe_state_epoch() < homing_safe_epoch)
            throw std::runtime_error("safe state did not invalidate home reference");
        image.write_from_device(statusword, 0x0027U);
        image.write_from_device(mode_display, 8U);
        home_enable.sequence = homing_exchange.next_command_sequence();
        if (!homing_exchange.try_enqueue(home_enable)) throw std::runtime_error("homing re-enable queue failed");
        static_cast<void>(homing_cycle.process(image));
        home.sequence = homing_exchange.next_command_sequence();
        if (!homing_exchange.try_enqueue(home)) throw std::runtime_error("second home queue failed");
        static_cast<void>(homing_cycle.process(image));
        image.write_from_device(mode_display, 6U);
        static_cast<void>(homing_cycle.process(image));
        image.write_from_device(statusword, 0x2027U);
        home_snapshot = homing_cycle.process(image);
        if (!home_snapshot.axes[0].homing_failed || home_snapshot.axes[0].homed
            || home_snapshot.axes[0].state != ecmotion::RealtimeAxisState::ErrorStop
            || image.read_unsigned(controlword) == 0x001FU)
            throw std::runtime_error("homing error did not fail safe");
        image.write_from_device(statusword, 0x0027U);
        image.write_from_device(mode_display, 8U);
        ecmotion::RealtimeCommand home_reset{
            .sequence = homing_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Reset};
        if (!homing_exchange.try_enqueue(home_reset)) throw std::runtime_error("homing reset queue failed");
        home_snapshot = homing_cycle.process(image);
        if (home_snapshot.axes[0].homing_failed || home_snapshot.axes[0].homed)
            throw std::runtime_error("reset did not clear failed homing while keeping axis unreferenced");
        home_enable.sequence = homing_exchange.next_command_sequence();
        if (!homing_exchange.try_enqueue(home_enable)) throw std::runtime_error("homing third enable queue failed");
        static_cast<void>(homing_cycle.process(image));
        home.sequence = homing_exchange.next_command_sequence();
        if (!homing_exchange.try_enqueue(home)) throw std::runtime_error("third home queue failed");
        static_cast<void>(homing_cycle.process(image));
        image.write_from_device(mode_display, 6U);
        image.write_from_device(statusword, 0x1427U); // Stale/instant attained never provides a fresh unattained sample.
        for (int tick = 0; tick < 1002; ++tick) home_snapshot = homing_cycle.process(image);
        if (!home_snapshot.axes[0].homing_failed || home_snapshot.axes[0].homed
            || image.read_unsigned(controlword) == 0x001FU)
            throw std::runtime_error("homing timeout did not fail safe");
        }

        TestAxis first_axis(1.0), second_axis(2.0);
        ecmotion::RealtimeExchange multi_exchange;
        ecmotion::PdoIoCycle multi_io(bound, image);
        ecmotion::FieldbusCycle multi_cycle(multi_exchange, multi_io,
            std::vector<ecmotion::RealtimeAxisAdapter*>{&first_axis, &second_axis});
        multi_cycle.set_bus_ready(true);
        const ecmotion::RealtimeCommand second_enable{
            .sequence = multi_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Enable, .axis_index = 1};
        if (!multi_exchange.try_enqueue(second_enable)) throw std::runtime_error("multi-axis queue failed");
        static_cast<void>(multi_cycle.process(image));
        const auto multi_snapshot = multi_exchange.snapshot();
        if (multi_snapshot.axis_count != 2 || first_axis.enabled() || !second_axis.enabled()
            || multi_snapshot.axes[0].actual_position != 1.0
            || multi_snapshot.axes[1].actual_position != 2.0) {
            throw std::runtime_error("multi-axis routing or snapshot isolation failed");
        }
        ecmotion::FieldbusControlBackend multi_backend(multi_exchange, {}, {"Left", "Right"});
        multi_backend.set_bus_ready(true);
        if (multi_backend.axis_snapshot("Right").actual_position != 2.0
            || multi_backend.axis_names().size() != 2) {
            throw std::runtime_error("multi-axis protocol lookup failed");
        }
        const ecmotion::RealtimeCommand unknown_axis{
            .sequence = multi_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Enable, .axis_index = 2};
        if (!multi_exchange.try_enqueue(unknown_axis)) throw std::runtime_error("unknown-axis queue failed");
        static_cast<void>(multi_cycle.process(image));
        if (multi_exchange.rejected_sequence() != unknown_axis.sequence || first_axis.enabled()) {
            throw std::runtime_error("unknown axis was not isolated and rejected");
        }
        ecmotion::RealtimeCommand group_line{
            .sequence = multi_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::GroupLine,
            .axis_index = 0, .secondary_axis_index = 1,
            .position = 5.0, .secondary_position = 7.0,
            .velocity = 10.0, .acceleration = 50.0, .deceleration = 50.0};
        if (!multi_exchange.try_enqueue(group_line)) throw std::runtime_error("group command queue failed");
        static_cast<void>(multi_cycle.process(image));
        if (multi_exchange.rejected_sequence() != group_line.sequence
            || first_axis.group_start_position() != 1.0
            || second_axis.group_start_position() != 2.0)
            throw std::runtime_error("group command was not rejected atomically");
        const ecmotion::RealtimeCommand first_enable{
            .sequence = multi_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Enable};
        if (!multi_exchange.try_enqueue(first_enable)) throw std::runtime_error("first-axis enable failed");
        static_cast<void>(multi_cycle.process(image));
        group_line.sequence = multi_exchange.next_command_sequence();
        if (!multi_exchange.try_enqueue(group_line)) throw std::runtime_error("group command queue failed");
        const auto group_started = multi_cycle.process(image);
        if (multi_exchange.rejected_sequence() == group_line.sequence
            || group_started.axes[0].motion_id != group_line.sequence
            || group_started.axes[1].motion_id != group_line.sequence
            || !group_started.axes[0].motion_busy || !group_started.axes[1].motion_busy)
            throw std::runtime_error("two axes did not start in the same realtime cycle");
        for (int index = 0; index < 5'000 && !first_axis.group_done(group_line.sequence); ++index)
            static_cast<void>(multi_cycle.process(image));
        if (!first_axis.group_done(group_line.sequence)
            || !second_axis.group_done(group_line.sequence)
            || std::abs(first_axis.group_start_position() - 5.0) > 1e-9
            || std::abs(second_axis.group_start_position() - 7.0) > 1e-9)
            throw std::runtime_error("synchronized group line did not finish together");
        group_line.sequence = multi_exchange.next_command_sequence();
        group_line.position = 9.0;
        group_line.secondary_position = 11.0;
        if (!multi_exchange.try_enqueue(group_line)) throw std::runtime_error("second group queue failed");
        static_cast<void>(multi_cycle.process(image));
        const ecmotion::RealtimeCommand group_stop{
            .sequence = multi_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Stop};
        if (!multi_exchange.try_enqueue(group_stop)) throw std::runtime_error("group stop queue failed");
        const auto stopped_group = multi_cycle.process(image);
        if (!stopped_group.axes[0].motion_aborted || !stopped_group.axes[1].motion_aborted
            || first_axis.enabled() || second_axis.enabled())
            throw std::runtime_error("group stop did not abort both axes");
        static_cast<void>(multi_exchange.request_safe_state());
        static_cast<void>(multi_cycle.process(image));
        if (second_axis.enabled()) throw std::runtime_error("safe request did not stop every axis");
        TestAxis spatial_x(0), spatial_y(1), spatial_z(0), spatial_a(5);
        ecmotion::RealtimeExchange spatial_exchange;
        ecmotion::PdoIoCycle spatial_io(bound, image);
        ecmotion::FieldbusCycle spatial_cycle(spatial_exchange, spatial_io,
            std::vector<ecmotion::RealtimeAxisAdapter*>{
                &spatial_x, &spatial_y, &spatial_z, &spatial_a});
        spatial_cycle.set_bus_ready(true);
        for (std::uint16_t slot = 0; slot < 4; ++slot) {
            const ecmotion::RealtimeCommand enable_spatial{
                .sequence = spatial_exchange.next_command_sequence(),
                .kind = ecmotion::RealtimeCommandKind::Enable, .axis_index = slot};
            if (!spatial_exchange.try_enqueue(enable_spatial))
                throw std::runtime_error("spatial enable queue failed");
        }
        static_cast<void>(spatial_cycle.process(image));
        const ecmotion::RealtimeCommand spatial_arc{
            .sequence = spatial_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::GroupSpatialArc,
            .axis_index = 0, .secondary_axis_index = 1,
            .third_axis_index = 2, .fourth_axis_index = 3, .group_axis_count = 4,
            .position = 0.0, .secondary_position = 0.0,
            .third_position = 1.0, .fourth_position = 9.0,
            .velocity = 1.0, .acceleration = 2.0, .deceleration = 2.0,
            .normal_x = 1.0, .normal_z = 0.0};
        if (!spatial_exchange.try_enqueue(spatial_arc))
            throw std::runtime_error("spatial arc queue failed");
        const auto spatial_started = spatial_cycle.process(image);
        for (std::size_t slot = 0; slot < 4; ++slot)
            if (spatial_started.axes[slot].motion_id != spatial_arc.sequence
                || !spatial_started.axes[slot].motion_busy)
                throw std::runtime_error("four axes did not start in one cycle");
        for (int index = 0; index < 5'000 && !spatial_a.group_done(spatial_arc.sequence); ++index)
            static_cast<void>(spatial_cycle.process(image));
        if (!spatial_x.group_done(spatial_arc.sequence)
            || !spatial_y.group_done(spatial_arc.sequence)
            || !spatial_z.group_done(spatial_arc.sequence)
            || !spatial_a.group_done(spatial_arc.sequence)
            || std::abs(spatial_z.group_start_position()-1.0) > 1e-9
            || std::abs(spatial_a.group_start_position()-9.0) > 1e-9)
            throw std::runtime_error("four-axis spatial arc did not finish together");
        ecmotion::RealtimeCommand spatial_line{
            .sequence = spatial_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::GroupSpatialLine,
            .axis_index = 0, .secondary_axis_index = 1,
            .third_axis_index = 2, .group_axis_count = 3,
            .position = 3.0, .secondary_position = 4.0, .third_position = 12.0,
            .velocity = 10.0, .acceleration = 20.0, .deceleration = 20.0};
        if (!spatial_exchange.try_enqueue(spatial_line))
            throw std::runtime_error("spatial line queue failed");
        const auto spatial_line_started = spatial_cycle.process(image);
        if (!spatial_line_started.axes[0].motion_busy
            || !spatial_line_started.axes[1].motion_busy
            || !spatial_line_started.axes[2].motion_busy
            || spatial_line_started.axes[3].motion_id != spatial_arc.sequence)
            throw std::runtime_error("three-axis line incorrectly routed its participants");
        for (int index = 0; index < 5'000 && !spatial_z.group_done(spatial_line.sequence); ++index)
            static_cast<void>(spatial_cycle.process(image));
        if (!spatial_x.group_done(spatial_line.sequence)
            || !spatial_y.group_done(spatial_line.sequence)
            || !spatial_z.group_done(spatial_line.sequence)
            || std::abs(spatial_a.group_start_position()-9.0) > 1e-9)
            throw std::runtime_error("three-axis line did not preserve the fourth axis");
        std::vector<TestAxis> maximum_axes;
        maximum_axes.reserve(ecmotion::RealtimeSnapshot::maximum_axes);
        std::vector<ecmotion::RealtimeAxisAdapter*> maximum_axis_pointers;
        std::vector<std::string> maximum_axis_names;
        for (std::size_t index = 0; index < ecmotion::RealtimeSnapshot::maximum_axes; ++index) {
            maximum_axes.emplace_back(static_cast<double>(index));
            maximum_axis_pointers.push_back(&maximum_axes.back());
            maximum_axis_names.push_back("Axis" + std::to_string(index + 1));
        }
        ecmotion::RealtimeExchange maximum_exchange;
        ecmotion::PdoIoCycle maximum_io(bound, image);
        ecmotion::FieldbusCycle maximum_cycle(
            maximum_exchange, maximum_io, std::move(maximum_axis_pointers));
        maximum_cycle.set_bus_ready(true);
        const ecmotion::RealtimeCommand last_axis_enable{
            .sequence = maximum_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Enable, .axis_index = 255};
        if (!maximum_exchange.try_enqueue(last_axis_enable)) throw std::runtime_error("capacity queue failed");
        static_cast<void>(maximum_cycle.process(image));
        const auto capacity_snapshot = maximum_exchange.snapshot();
        if (capacity_snapshot.axis_count != 256 || !maximum_axes.back().enabled()
            || maximum_axes.front().enabled() || capacity_snapshot.axes[255].actual_position != 255.0) {
            throw std::runtime_error("256-axis capacity or high-index routing failed");
        }
        ecmotion::FieldbusControlBackend maximum_backend(
            maximum_exchange, {}, std::move(maximum_axis_names));
        if (maximum_backend.axis_snapshot("Axis256").actual_position != 255.0) {
            throw std::runtime_error("highest axis name was not mapped to snapshot index 255");
        }
        const ecmotion::RealtimeCommand out_of_range{
            .sequence = maximum_exchange.next_command_sequence(),
            .kind = ecmotion::RealtimeCommandKind::Enable, .axis_index = 256};
        if (!maximum_exchange.try_enqueue(out_of_range)) throw std::runtime_error("range queue failed");
        static_cast<void>(maximum_cycle.process(image));
        if (maximum_exchange.rejected_sequence() != out_of_range.sequence) {
            throw std::runtime_error("axis index 256 was not rejected");
        }
        const auto outside_limit_counts = static_cast<std::uint32_t>(std::llround(
            501.0 * commissioned_configuration.counts_per_engineering_unit));
        image.write_from_device(
            layout.resolve(axis_configuration.position_actual_key), outside_limit_counts);
        const auto limit_snapshot = commissioned_cycle.process(image);
        if (!limit_snapshot.software_limit_violation
            || image.read_unsigned(controlword) == 0x000FU) {
            throw std::runtime_error("actual-position soft-limit interlock did not remove enable");
        }
        std::vector<std::string> output_names;
        for (std::size_t index = 0; index < 16U; ++index) {
            output_names.push_back("MainIo.DO." + std::to_string(index));
        }
        ecmotion::FieldbusControlBackend fieldbus_backend(exchange, std::move(output_names));
        fieldbus_backend.set_bus_ready(true);
        const auto protocol_axis = fieldbus_backend.axis_snapshot("Axis1");
        if (std::abs(protocol_axis.actual_position - 10.0) > 1e-9
            || protocol_axis.state != "Standstill"
            || protocol_axis.fault.find("commissioning") == std::string::npos
            || fieldbus_backend.runtime_ready() || fieldbus_backend.motion_ready()
            || !fieldbus_backend.io_ready()
            || fieldbus_backend.digital_outputs().size() != 16U) {
            throw std::runtime_error("fieldbus protocol backend did not expose the realtime snapshot safely");
        }
        ecmotion::RealtimeBatch output_batch;
        output_batch.count = 2;
        const auto batch_sequence = exchange.next_command_sequence();
        output_batch.commands[0] = {.sequence=batch_sequence, .kind=ecmotion::RealtimeCommandKind::WriteOutputs,
            .output_mask=1, .output_values=1};
        output_batch.commands[1] = {.sequence=batch_sequence, .kind=ecmotion::RealtimeCommandKind::WriteOutputs,
            .output_mask=2, .output_values=2};
        if (!exchange.try_enqueue_batch(output_batch)) throw std::runtime_error("Batch queue failed");
        const auto applied_batch = fieldbus_cycle.process(image, 100);
        if ((applied_batch.digital_outputs & 3) != 3
            || applied_batch.last_applied_command_cycle != applied_batch.cycles)
            throw std::runtime_error("Outputs were not applied in one fieldbus cycle");
        const auto invalid_sequence = exchange.next_command_sequence();
        output_batch.commands[0].sequence = output_batch.commands[1].sequence = invalid_sequence;
        output_batch.commands[0].output_values = 0;
        output_batch.commands[1].output_mask = std::uint64_t{1} << 62;
        output_batch.commands[1].output_values = 0;
        if (!exchange.try_enqueue_batch(output_batch)) throw std::runtime_error("Invalid batch queue failed");
        const auto unchanged = fieldbus_cycle.process(image, 101);
        if ((unchanged.digital_outputs & 3) != 3 || exchange.rejected_sequence() != invalid_sequence)
            throw std::runtime_error("Invalid batch partially changed output image");
        const auto cancelled_sequence = exchange.next_command_sequence();
        output_batch.commands[0].sequence = output_batch.commands[1].sequence = cancelled_sequence;
        output_batch.commands[0].output_values = 1;
        output_batch.commands[1].output_mask = output_batch.commands[1].output_values = 2;
        if (!exchange.try_enqueue_batch(output_batch)) throw std::runtime_error("Stop race queue failed");
        exchange.cancel_through(cancelled_sequence);
        static_cast<void>(exchange.request_safe_state());
        if (fieldbus_cycle.process(image, 102).digital_outputs != 0 || exchange.rejected_sequence() != cancelled_sequence)
            throw std::runtime_error("Stopped batch was applied after safe-state request");
        const auto expired_sequence = exchange.next_command_sequence();
        output_batch.commands[0].sequence = output_batch.commands[1].sequence = expired_sequence;
        output_batch.deadline_ns = 1;
        if (!exchange.try_enqueue_batch(output_batch)) throw std::runtime_error("Expired batch queue failed");
        if (fieldbus_cycle.process(image, 103).digital_outputs != 0 || exchange.rejected_sequence() != expired_sequence)
            throw std::runtime_error("Expired batch was applied");
        ecmotion::RealtimeExchange bounded;
        unsigned queued{};
        while (queued < 1000 && bounded.try_enqueue_batch(output_batch)) ++queued;
        if (queued != 63) throw std::runtime_error("Batch queue is not bounded to 63 usable slots");
        ecmotion::RealtimeCommand legacy;
        if (bounded.try_dequeue(legacy)) throw std::runtime_error("Legacy consumer discarded a multi-action batch");
        ecmotion::RealtimeBatch dequeued;
        for (unsigned i=0; i<queued; ++i)
            if (!bounded.try_dequeue_batch(dequeued) || dequeued.count != 2)
                throw std::runtime_error("Bounded queue lost a complete batch");
        if (bounded.try_dequeue_batch(dequeued)) throw std::runtime_error("Queue retained stale batch");
        std::puts("PASS strict native IO map, atomic scan batches, stop cancellation and bounded queue");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL native IO map: %s\n", error.what());
        return 1;
    }
}

#include <ecrt.h>

#include "axis_feedback.hpp"
#include "cyclic_trace.hpp"
#include "delivery_monitor.hpp"
#include "ethercat_runtime_config.hpp"
#include "sha256.hpp"
#include "slave_identity.hpp"
#include <fstream>
#include <iterator>
#ifdef ECMOTION_IS620N_PROFILE
#include "is620n_ethercat_config.hpp"
#else
#include "ethercat_config.hpp"
#endif
#include "fieldbus_control_backend.hpp"
#include "fieldbus_cycle.hpp"
#include "io_map_config.hpp"
#include "native_protocol_server.hpp"
#include "pdo_io_cycle.hpp"
#include "pdo_image.hpp"

#include <array>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cerrno>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <limits>
#include <memory>
#include <optional>
#include <nlohmann/json.hpp>
#include <sched.h>
#include <sys/mman.h>
#include <time.h>
#include <thread>
#include <string>
#include <unistd.h>
#include <vector>

namespace legacy_cfg = ecmotion::generated;
namespace cfg = ecmotion::runtime_config;
cfg::Configuration active_config;
cfg::Configuration legacy_configuration() {
    cfg::Configuration result;
    result.ready_for_runtime=legacy_cfg::ready_for_runtime;
    result.blocker_count=legacy_cfg::blocker_count;
    result.cycle_time_ns=legacy_cfg::cycle_time_ns;
    result.topology_fingerprint=legacy_cfg::topology_fingerprint;
    for(const auto& e:legacy_cfg::entries) result.entries.push_back({e.index,e.subindex,e.bits,e.key});
    for(const auto& p:legacy_cfg::pdos) result.pdos.push_back({p.direction==legacy_cfg::Direction::Rx?cfg::Direction::Rx:cfg::Direction::Tx,p.sync_manager,p.index,p.entry_begin,p.entry_count});
    result.module_idents.assign(legacy_cfg::module_idents.begin(),legacy_cfg::module_idents.end());
    for(const auto& s:legacy_cfg::slaves) {
        cfg::Slave slave{s.position,s.vendor,s.product,s.revision,s.name,{s.dc.enabled,s.dc.assign_activate,s.dc.sync0_cycle_ns,s.dc.sync0_shift_ns,s.dc.sync1_cycle_ns},s.pdo_begin,s.pdo_count,s.module_begin,s.module_count,{}, {}};
        slave.sync_managers={{2,cfg::Direction::Rx,cfg::Watchdog::Enable},{3,cfg::Direction::Tx,cfg::Watchdog::Disable}};
        result.slaves.push_back(std::move(slave));
    }
    return result;
}

namespace {
volatile std::sig_atomic_t running = 1;
std::atomic<unsigned> trace_signal{0};
static_assert(std::atomic<unsigned>::is_always_lock_free);
void trace_handler(int number) noexcept {trace_signal.store(number==SIGUSR1?1U:2U,std::memory_order_relaxed);}
std::atomic_bool protocol_failed{false};

struct SlaveDiagnostic {
    std::atomic_uint al_state{0};
    std::atomic_bool online{false};
    std::atomic_bool operational{false};
};

struct RuntimeDiagnostics {
    std::atomic_uint slaves_responding{0};
    std::atomic_uint master_al_states{0};
    std::atomic_bool link_up{false};
    std::atomic_uint working_counter{0};
    std::atomic_uint wc_state{EC_WC_ZERO};
    std::atomic_uint64_t cycles{0};
    std::atomic_uint64_t overruns{0};
    std::atomic_int64_t maximum_lateness_ns{0};
    std::atomic_uint64_t execution_overruns{0};
    std::atomic_int64_t last_execution_ns{0};
    std::atomic_int64_t maximum_execution_ns{0};
    std::atomic_uint dc_difference_ns{0};
    std::atomic_uint maximum_dc_difference_ns{0};
    explicit RuntimeDiagnostics(std::size_t count) : slaves(count) {}
    std::vector<SlaveDiagnostic> slaves;
};

void stop_handler(int) noexcept { running = 0; }

struct MasterReleaser {
    void operator()(ec_master_t* master) const noexcept {
        if (master != nullptr) ecrt_release_master(master);
    }
};

using MasterHandle = std::unique_ptr<ec_master_t, MasterReleaser>;

[[noreturn]] void fail(const char* message) {
    std::fprintf(stderr, "ecmotion-rt: %s\n", message);
    std::exit(EXIT_FAILURE);
}

void require_zero(int result, const char* operation) {
    if (result != 0) {
        std::fprintf(stderr, "ecmotion-rt: %s failed (%d)\n", operation, result);
        std::exit(EXIT_FAILURE);
    }
}

ecmotion::IoMapConfiguration load_io_configuration(const std::string& path) {
    try {
        return ecmotion::IoMapConfiguration::load(path);
    } catch (const std::exception& error) {
        std::fprintf(stderr, "ecmotion-rt: cannot load IO map '%s': %s\n", path.c_str(), error.what());
        std::exit(EXIT_FAILURE);
    }
}

std::vector<ecmotion::AxisFeedbackConfiguration> load_axis_configuration(const std::string& path) {
    try {
        return ecmotion::AxisFeedbackConfiguration::load_all(path);
    } catch (const std::exception& error) {
        std::fprintf(stderr, "ecmotion-rt: cannot load axis configuration '%s': %s\n",
            path.c_str(), error.what());
        std::exit(EXIT_FAILURE);
    }
}

std::optional<csp_timing::DeliveryConfig> load_cycle_timing(const std::string& path,
    const std::vector<ecmotion::AxisFeedbackConfiguration>& axes) {
    std::ifstream input(path);
    nlohmann::json root; input >> root;
    if (!root.contains("cycleTiming")) return std::nullopt;
    const auto& timing = root.at("cycleTiming");
    if (!timing.is_object()) throw std::invalid_argument("cycleTiming must be an object");
    if (!timing.contains("enabled") || !timing.at("enabled").is_boolean())
        throw std::invalid_argument("cycleTiming.enabled must be boolean");
    if (!timing.at("enabled").get<bool>()) return std::nullopt;
    if (timing.value("model", std::string{}) != "R2M")
        throw std::invalid_argument("cycleTiming requires explicit model R2M");
    const auto positive_integer = [&](const char* key, std::uint64_t fallback) {
        if (!timing.contains(key)) return fallback;
        const auto& value = timing.at(key);
        if (!value.is_number_unsigned() &&
            !(value.is_number_integer() && value.get<std::int64_t>() > 0))
            throw std::invalid_argument(std::string("cycleTiming.") + key + " must be a positive integer");
        const auto result = value.get<std::uint64_t>();
        if (result == 0) throw std::invalid_argument("cycleTiming budget/count must be positive");
        return result;
    };
    if (axes.empty() || active_config.slaves.empty() || !active_config.slaves.front().dc.enabled)
        throw std::invalid_argument("cycleTiming requires configured axes and a DC reference slave");
    const auto& reference_dc = active_config.slaves.front().dc;
    if (reference_dc.sync0_cycle_ns != active_config.cycle_time_ns || reference_dc.sync0_shift_ns < 0)
        throw std::invalid_argument("cycleTiming reference DC period/shift mismatch");
    for (const auto& axis : axes) {
        const auto slave = std::find_if(active_config.slaves.begin(), active_config.slaves.end(),
            [&](const auto& value) { return value.position == axis.slave_position; });
        if (slave == active_config.slaves.end() || !slave->dc.enabled ||
            slave->dc.sync0_cycle_ns != reference_dc.sync0_cycle_ns ||
            slave->dc.sync0_shift_ns != reference_dc.sync0_shift_ns)
            throw std::invalid_argument("cycleTiming axes require a common enabled DC period/shift");
    }
    csp_timing::DeliveryConfig config;
    config.period_ns = active_config.cycle_time_ns;
    config.sync_shift_ns = static_cast<std::uint64_t>(reference_dc.sync0_shift_ns);
    config.boundary_guard_ns = positive_integer("boundaryGuardNs", 200000);
    config.remaining_send_budget_ns = positive_integer("remainingSendBudgetNs", 100000);
    config.required_good_cycles = positive_integer("requiredGoodCycles", 1000);
    static_cast<void>(csp_timing::DeliveryMonitor(config)); // validate before master access
    return config;
}

void validate_axis_topology(const std::vector<ecmotion::AxisFeedbackConfiguration>& axes) {
    for (const auto& axis : axes) {
        const auto found = std::find_if(active_config.slaves.begin(), active_config.slaves.end(),
            [&](const cfg::Slave& slave) { return slave.position == axis.slave_position; });
        if (found == active_config.slaves.end()) fail("axis slavePosition is absent from generated topology");
        const std::array<std::string, 9> keys{axis.controlword_key, axis.target_position_key,
            axis.velocity_offset_key, axis.mode_of_operation_key, axis.statusword_key,
            axis.position_actual_key, axis.velocity_actual_key, axis.mode_display_key,
            axis.error_code_key};
        for (std::size_t key_index=0;key_index<keys.size();++key_index) {
            const auto& key=keys[key_index];
            constexpr std::array<unsigned,9> widths{16,32,32,8,16,32,32,8,16};
            bool belongs = false;
            for (std::size_t p = found->pdo_begin; p < found->pdo_begin + found->pdo_count; ++p) {
                const auto& pdo = active_config.pdos[p];
                for (std::size_t e = pdo.entry_begin; e < pdo.entry_begin + pdo.entry_count; ++e) {
                    if(key == active_config.entries[e].key) {
                        const auto expected_direction=key_index<4?cfg::Direction::Rx:cfg::Direction::Tx;
                        if(active_config.entries[e].bits!=widths[key_index]||pdo.direction!=expected_direction) fail("axis PDO width/direction differs from CiA402 ABI");
                        belongs=true;
                    }
                }
            }
            if (!belongs) fail("axis PDO key does not belong to configured slavePosition");
        }
    }
}

void configure_realtime(int cpu) {
    if (mlockall(MCL_CURRENT | MCL_FUTURE) != 0) fail("mlockall failed; grant CAP_IPC_LOCK");

    cpu_set_t set;
    CPU_ZERO(&set);
    CPU_SET(cpu, &set);
    if (sched_setaffinity(0, sizeof(set), &set) != 0) fail("sched_setaffinity failed");

    sched_param parameters{};
    parameters.sched_priority = 80;
    if (sched_setscheduler(0, SCHED_FIFO, &parameters) != 0) {
        fail("SCHED_FIFO failed; grant CAP_SYS_NICE");
    }
}

timespec add_nanoseconds(timespec value, std::uint32_t nanoseconds) noexcept {
    value.tv_nsec += static_cast<long>(nanoseconds);
    while (value.tv_nsec >= 1'000'000'000L) {
        value.tv_nsec -= 1'000'000'000L;
        ++value.tv_sec;
    }
    return value;
}

std::uint64_t to_nanoseconds(const timespec& value) noexcept {
    return static_cast<std::uint64_t>(value.tv_sec) * 1'000'000'000ULL
        + static_cast<std::uint64_t>(value.tv_nsec);
}

std::int64_t difference_nanoseconds(const timespec& left, const timespec& right) noexcept {
    return (static_cast<std::int64_t>(left.tv_sec) - static_cast<std::int64_t>(right.tv_sec))
            * 1'000'000'000LL
        + static_cast<std::int64_t>(left.tv_nsec) - static_cast<std::int64_t>(right.tv_nsec);
}

void update_maximum(std::atomic_int64_t& target, std::int64_t value) noexcept {
    auto current = target.load(std::memory_order_relaxed);
    while (value > current
           && !target.compare_exchange_weak(current, value, std::memory_order_relaxed)) {
    }
}

void sample_bus_state(
    ec_master_t* master,
    ec_domain_t* domain,
    const std::vector<ec_slave_config_t*>& slave_configs,
    RuntimeDiagnostics& diagnostics) noexcept {
    ec_master_state_t master_state{};
    ecrt_master_state(master, &master_state);
    diagnostics.slaves_responding.store(master_state.slaves_responding, std::memory_order_relaxed);
    diagnostics.master_al_states.store(master_state.al_states, std::memory_order_relaxed);
    diagnostics.link_up.store(master_state.link_up != 0, std::memory_order_relaxed);

    ec_domain_state_t domain_state{};
    ecrt_domain_state(domain, &domain_state);
    diagnostics.working_counter.store(domain_state.working_counter, std::memory_order_relaxed);
    diagnostics.wc_state.store(domain_state.wc_state, std::memory_order_relaxed);

    for (std::size_t index = 0; index < slave_configs.size(); ++index) {
        ec_slave_config_state_t state{};
        ecrt_slave_config_state(slave_configs[index], &state);
        diagnostics.slaves[index].al_state.store(state.al_state, std::memory_order_relaxed);
        diagnostics.slaves[index].online.store(state.online != 0, std::memory_order_relaxed);
        diagnostics.slaves[index].operational.store(state.operational != 0, std::memory_order_relaxed);
    }
}

bool bus_is_ready(const RuntimeDiagnostics& diagnostics) noexcept {
    if (!diagnostics.link_up.load(std::memory_order_relaxed)
        || diagnostics.slaves_responding.load(std::memory_order_relaxed) != active_config.slaves.size()
        || diagnostics.wc_state.load(std::memory_order_relaxed) != EC_WC_COMPLETE) {
        return false;
    }
    for (const auto& slave : diagnostics.slaves) {
        if (!slave.operational.load(std::memory_order_relaxed)) return false;
    }
    return true;
}

std::vector<std::string> digital_output_names(const ecmotion::IoMapConfiguration& configuration) {
    std::vector<std::string> result;
    for (const auto& group : configuration.groups()) {
        if (group.direction != ecmotion::LogicalIoDirection::Output
            || group.encoding != ecmotion::LogicalEncoding::PackedBoolean) continue;
        for (std::size_t channel = 0; channel < group.channel_count; ++channel) {
            result.push_back(group.name_prefix + "." + std::to_string(channel));
        }
    }
    return result;
}

std::vector<std::string> digital_input_names(const ecmotion::IoMapConfiguration& configuration) {
    std::vector<std::string> result;
    for (const auto& group : configuration.groups()) {
        if (group.direction != ecmotion::LogicalIoDirection::Input
            || group.encoding != ecmotion::LogicalEncoding::PackedBoolean) continue;
        for (std::size_t channel = 0; channel < group.channel_count; ++channel) {
            result.push_back(group.name_prefix + "." + std::to_string(channel));
        }
    }
    return result;
}

void diagnostics_loop(const RuntimeDiagnostics& diagnostics) {
    while (running != 0) {
        std::this_thread::sleep_for(std::chrono::seconds(1));
        std::fprintf(stderr,
            "ecmotion diag: cycles=%llu overruns=%llu max_late_ns=%lld "
            "exec_overruns=%llu last_exec_ns=%lld max_exec_ns=%lld "
            "dc_diff_ns=%u max_dc_diff_ns=%u link=%u responding=%u al=0x%02X wc=%u wc_state=%u\n",
            static_cast<unsigned long long>(diagnostics.cycles.load(std::memory_order_relaxed)),
            static_cast<unsigned long long>(diagnostics.overruns.load(std::memory_order_relaxed)),
            static_cast<long long>(diagnostics.maximum_lateness_ns.load(std::memory_order_relaxed)),
            static_cast<unsigned long long>(diagnostics.execution_overruns.load(std::memory_order_relaxed)),
            static_cast<long long>(diagnostics.last_execution_ns.load(std::memory_order_relaxed)),
            static_cast<long long>(diagnostics.maximum_execution_ns.load(std::memory_order_relaxed)),
            diagnostics.dc_difference_ns.load(std::memory_order_relaxed),
            diagnostics.maximum_dc_difference_ns.load(std::memory_order_relaxed),
            diagnostics.link_up.load(std::memory_order_relaxed) ? 1U : 0U,
            diagnostics.slaves_responding.load(std::memory_order_relaxed),
            diagnostics.master_al_states.load(std::memory_order_relaxed),
            diagnostics.working_counter.load(std::memory_order_relaxed),
            diagnostics.wc_state.load(std::memory_order_relaxed));

        for (std::size_t index = 0; index < diagnostics.slaves.size(); ++index) {
            const auto& slave = diagnostics.slaves[index];
            std::fprintf(stderr, "  slave[%zu] online=%u operational=%u al=0x%02X\n",
                index,
                slave.online.load(std::memory_order_relaxed) ? 1U : 0U,
                slave.operational.load(std::memory_order_relaxed) ? 1U : 0U,
                slave.al_state.load(std::memory_order_relaxed));
        }
    }
}

void verify_identity(ec_master_t* master, const cfg::Slave& expected) {
    const ecmotion::SlaveIdentity identity{expected.position, expected.vendor,
        expected.product, expected.revision};
    const auto result = ecmotion::wait_for_slave_identity(identity, [&]() -> std::optional<ecmotion::SlaveIdentity> {
        ec_slave_info_t actual{};
        if (ecrt_master_get_slave(master, expected.position, &actual) != 0) return std::nullopt;
        return ecmotion::SlaveIdentity{expected.position, actual.vendor_id,
            actual.product_code, actual.revision_number};
    }, [] { std::this_thread::sleep_for(std::chrono::milliseconds(20)); }, 250);
    if (result.status == ecmotion::SlaveIdentityStatus::Unavailable) {
        fail("configured slave was not discovered within 5 seconds; check EtherCAT link and scan");
    }
    if (result.status == ecmotion::SlaveIdentityStatus::Mismatch) {
        const auto& actual = *result.actual;
        std::fprintf(stderr,
            "ecmotion-rt: identity mismatch at position %u: "
            "actual=%08X/%08X/%08X expected=%08X/%08X/%08X\n",
            expected.position, actual.vendor, actual.product, actual.revision,
            expected.vendor, expected.product, expected.revision);
        std::exit(EXIT_FAILURE);
    }
}
} // namespace

int main(int argc, char** argv) {
    active_config=legacy_configuration();
    int cpu = 2;
    std::string io_map_path = "/etc/ecmotion/io-map.json";
    std::string machine_config_path = "/etc/ecmotion/reference-machine.json";
    std::string socket_path = "/run/ecmotion/controller.sock";
    std::string ethercat_config_path;
    std::string axis_trace_path;
    std::string axis_trace_prefix="Axis1Drive";
    std::size_t axis_trace_capacity=60000;
    bool validate_only=false;
    bool machine_explicit=false, io_explicit=false;
    for (int index = 1; index < argc; index += 2) {
        if (index + 1 >= argc) fail("missing command-line option value");
        if (std::strcmp(argv[index], "--cpu") == 0) cpu = std::atoi(argv[index + 1]);
        else if (std::strcmp(argv[index], "--io-map") == 0) {io_map_path = argv[index + 1];io_explicit=true;}
        else if (std::strcmp(argv[index], "--machine-config") == 0) {machine_config_path = argv[index + 1];machine_explicit=true;}
        else if (std::strcmp(argv[index], "--ethercat-config") == 0) ethercat_config_path=argv[index+1];
        else if (std::strcmp(argv[index], "--validate-ethercat-config") == 0) {ethercat_config_path=argv[index+1];validate_only=true;}
        else if (std::strcmp(argv[index], "--axis-trace-csv") == 0) axis_trace_path=argv[index+1];
        else if (std::strcmp(argv[index], "--axis-trace-prefix") == 0) axis_trace_prefix=argv[index+1];
        else if (std::strcmp(argv[index], "--axis-trace-capacity") == 0) {
            const std::string value=argv[index+1];
            if(value.empty() || value.find_first_not_of("0123456789")!=std::string::npos)
                fail("trace capacity must be a positive integer");
            errno=0;char* end=nullptr;const auto count=std::strtoull(value.c_str(),&end,10);
            if(errno!=0 || !end || *end || count==0 || count>ecmotion::CyclicAxisTrace::maximum_capacity)
                fail("trace capacity must be 1..120000");
            axis_trace_capacity=static_cast<std::size_t>(count);
        }
        else if (std::strcmp(argv[index], "--socket") == 0) socket_path = argv[index + 1];
        else fail("unknown command-line option");
    }
    try {
        if(!ethercat_config_path.empty()) active_config=cfg::Configuration::load(ethercat_config_path);
        if(!active_config.ready_for_runtime) fail("generated topology has unresolved blockers");
        if(validate_only && !machine_explicit && !io_explicit && active_config.machine_sha256.empty()) {
            std::printf("EtherCAT topology valid: %s\n",active_config.topology_fingerprint.c_str());return EXIT_SUCCESS;
        }
        if(validate_only && (!machine_explicit || !io_explicit)) fail("offline bundle validation requires explicit --machine-config and --io-map");
        const auto verify_file=[](const std::string& path,const std::string& expected) {
            if(expected.empty()) return;
            std::ifstream input(path,std::ios::binary);if(!input) fail("cannot read configuration for hash binding");
            const std::string bytes{std::istreambuf_iterator<char>(input),std::istreambuf_iterator<char>()};
            if(ecmotion::sha256(bytes)!=expected) fail("machine/IO file SHA256 differs from EtherCAT bundle");
        };
        verify_file(machine_config_path,active_config.machine_sha256);
        verify_file(io_map_path,active_config.io_map_sha256);
    } catch(const std::exception& e) {fail(e.what());}
    if (cpu < 0 || cpu >= CPU_SETSIZE) fail("invalid realtime CPU index");
    const auto io_configuration = load_io_configuration(io_map_path);
    const auto axis_configurations = load_axis_configuration(machine_config_path);
    validate_axis_topology(axis_configurations);
    std::optional<csp_timing::DeliveryConfig> cycle_timing_config;
    std::optional<csp_timing::DeliveryMonitor> delivery_monitor;
    try {
        cycle_timing_config = load_cycle_timing(machine_config_path, axis_configurations);
        if (cycle_timing_config) delivery_monitor.emplace(*cycle_timing_config);
    } catch (const std::exception& error) { fail(error.what()); }
    for (const auto& axis : axis_configurations)
        for (const auto& slave : active_config.slaves) if (slave.position == axis.slave_position)
            for (std::size_t p = slave.pdo_begin; p < slave.pdo_begin + slave.pdo_count; ++p)
                for (std::size_t e = active_config.pdos[p].entry_begin; e < active_config.pdos[p].entry_begin + active_config.pdos[p].entry_count; ++e)
                    for (const auto& group : io_configuration.groups())
                        if (std::find(group.source_entries.begin(), group.source_entries.end(), active_config.entries[e].key)
                            != group.source_entries.end())
                            fail("Ordinary IO map references an axis-owned slave.");

    // Bind against a synthetic byte-aligned layout before taking ownership of the bus.
    // IgH supplies the real offsets later; key, width and direction validation is identical.
    try {
        std::vector<ecmotion::PdoEntryDescriptor> preflight_entries;
        std::size_t bytes=0;
        for(const auto& pdo:active_config.pdos) for(std::size_t e=pdo.entry_begin;e<pdo.entry_begin+pdo.entry_count;++e) {
            const auto& entry=active_config.entries[e];
            preflight_entries.push_back({entry.key,pdo.direction==cfg::Direction::Rx?ecmotion::PdoDirection::ControllerOutput:ecmotion::PdoDirection::ControllerInput,bytes,0,entry.bits});
            bytes+=(entry.bits+7U)/8U;
        }
        const ecmotion::PdoLayout preflight_layout(std::move(preflight_entries),bytes);
        for(const auto& axis:axis_configurations) {
            const auto cycle_seconds=static_cast<double>(active_config.cycle_time_ns)/1'000'000'000.0;
            if(std::abs(axis.motion.cycle_seconds-cycle_seconds)>cycle_seconds*1e-12) fail("axis cycle differs from EtherCAT master cycle");
            const auto adapter=ecmotion::make_axis_adapter(preflight_layout,axis);
            static_cast<void>(adapter);
        }
        const auto bound=io_configuration.bind(preflight_layout);
        if(bound.logical_channel_count==0U && axis_configurations.empty()) fail("IO map contains no logical channels and no axes are configured");
    } catch(const std::exception& error) {fail(error.what());}
    if(validate_only) {
        std::printf("EtherCAT bundle valid: %s\n",active_config.topology_fingerprint.c_str());return EXIT_SUCCESS;
    }

    MasterHandle master{ecrt_request_master(0)};
    if (!master) fail("cannot request IgH master 0");
    ec_domain_t* domain = ecrt_master_create_domain(master.get());
    if (domain == nullptr) fail("cannot create process data domain");

    std::vector<std::vector<ec_pdo_entry_info_t>> entry_storage(active_config.pdos.size());
    std::vector<ec_pdo_info_t> pdo_storage(active_config.pdos.size());
    std::vector<unsigned int> offsets(active_config.entries.size());
    std::vector<unsigned int> bit_positions(active_config.entries.size());
    std::vector<ecmotion::PdoDirection> entry_directions(
        active_config.entries.size(), ecmotion::PdoDirection::ControllerInput);
    std::vector<ec_slave_config_t*> slave_configs(active_config.slaves.size());

    for (std::size_t slave_index = 0; slave_index < active_config.slaves.size(); ++slave_index) {
        const auto& slave = active_config.slaves[slave_index];
        verify_identity(master.get(), slave);
        ec_slave_config_t* sc = ecrt_master_slave_config(
            master.get(), 0, slave.position, slave.vendor, slave.product);
        if (sc == nullptr) fail("cannot create slave configuration");
        slave_configs[slave_index] = sc;

        for(const auto& sdo:slave.startup_sdos) {
            const int result=sdo.complete_access
                ? ecrt_slave_config_complete_sdo(sc,sdo.index,sdo.data.data(),sdo.data.size())
                : ecrt_slave_config_sdo(sc,sdo.index,sdo.subindex,sdo.data.data(),sdo.data.size());
            require_zero(result,"configure startup SDO");
        }
        // Homing parameters are drive object values, commissioned per model and wired sensor.
        // They are configured before activation; no blocking SDO runs in the 1 ms cycle.
        for (const auto& axis : axis_configurations) {
            if (axis.slave_position != slave.position || axis.homing.method == 0) continue;
            require_zero(ecrt_slave_config_sdo8(sc, 0x6098, 0,
                static_cast<std::uint8_t>(axis.homing.method)), "configure homing method");
            require_zero(ecrt_slave_config_sdo32(sc, 0x6099, 1,
                axis.homing.search_speed_raw), "configure homing search speed");
            require_zero(ecrt_slave_config_sdo32(sc, 0x6099, 2,
                axis.homing.latch_speed_raw), "configure homing latch speed");
            require_zero(ecrt_slave_config_sdo32(sc, 0x609A, 0,
                axis.homing.acceleration_raw), "configure homing acceleration");
            require_zero(ecrt_slave_config_sdo32(sc, 0x607C, 0,
                static_cast<std::uint32_t>(axis.homing.offset_raw)),
                "configure homing offset");
        }

        for (std::size_t slot = 0; slot < slave.module_count; ++slot) {
            const auto subindex = slot + 1U;
            if (subindex > std::numeric_limits<std::uint8_t>::max()) fail("too many modular slots");
            require_zero(ecrt_slave_config_sdo32(sc, 0xF030,
                static_cast<std::uint8_t>(subindex), active_config.module_idents[slave.module_begin + slot]),
                "configure 0xF030 module ident");
        }

        for (std::size_t p = 0; p < slave.pdo_count; ++p) {
            const auto global_pdo = slave.pdo_begin + p;
            const auto& source_pdo = active_config.pdos[global_pdo];
            auto& destination_entries = entry_storage[global_pdo];
            destination_entries.reserve(source_pdo.entry_count);
            for (std::size_t e = 0; e < source_pdo.entry_count; ++e) {
                const auto& entry = active_config.entries[source_pdo.entry_begin + e];
                destination_entries.push_back({entry.index, entry.subindex, entry.bits});
            }
            pdo_storage[global_pdo] = {
                source_pdo.index,
                static_cast<unsigned int>(destination_entries.size()),
                destination_entries.data()};
        }

        std::array<ec_sync_info_t, 14> syncs{};
        std::size_t sync_count = 0;
        for (const auto& manager : slave.sync_managers) {
            const auto sync_index=manager.index;
            std::size_t begin = slave.pdo_begin;
            while (begin < slave.pdo_begin + slave.pdo_count
                   && active_config.pdos[begin].sync_manager != sync_index) ++begin;
            std::size_t count = 0;
            while (begin + count < slave.pdo_begin + slave.pdo_count
                   && active_config.pdos[begin + count].sync_manager == sync_index) ++count;
            if (count == 0) continue;
            syncs[sync_count++] = {
                sync_index,
                manager.direction == cfg::Direction::Rx ? EC_DIR_OUTPUT : EC_DIR_INPUT,
                static_cast<unsigned int>(count),
                &pdo_storage[begin],
                manager.watchdog == cfg::Watchdog::Enable ? EC_WD_ENABLE : manager.watchdog == cfg::Watchdog::Disable ? EC_WD_DISABLE : EC_WD_DEFAULT};
        }
        require_zero(ecrt_slave_config_pdos(sc, static_cast<unsigned int>(sync_count), syncs.data()),
            "configure PDOs");

        if (slave.dc.enabled) {
            require_zero(ecrt_slave_config_dc(sc, slave.dc.assign_activate,
                slave.dc.sync0_cycle_ns, slave.dc.sync0_shift_ns,
                slave.dc.sync1_cycle_ns, 0), "configure distributed clocks");
        }

        for (std::size_t p = 0; p < slave.pdo_count; ++p) {
            const auto& pdo = active_config.pdos[slave.pdo_begin + p];
            for (std::size_t e = 0; e < pdo.entry_count; ++e) {
                const auto global_entry = pdo.entry_begin + e;
                const auto& entry = active_config.entries[global_entry];
                entry_directions[global_entry] = pdo.direction == cfg::Direction::Rx
                    ? ecmotion::PdoDirection::ControllerOutput
                    : ecmotion::PdoDirection::ControllerInput;
                const int result = ecrt_slave_config_reg_pdo_entry(sc,
                    entry.index, entry.subindex, domain,
                    entry.bits % 8 == 0 ? nullptr : &bit_positions[global_entry]);
                if (result < 0) fail("register PDO entry failed");
                offsets[global_entry] = static_cast<unsigned int>(result);
            }
        }
    }

    if (!slave_configs.empty()) {
        require_zero(ecrt_master_select_reference_clock(master.get(), slave_configs.front()),
            "select reference clock");
    }
    require_zero(ecrt_master_activate(master.get()), "activate master");
    std::uint8_t* process_data = ecrt_domain_data(domain);
    if (process_data == nullptr) fail("domain process data is unavailable");

    std::vector<ecmotion::PdoEntryDescriptor> descriptors;
    descriptors.reserve(active_config.entries.size());
    for (std::size_t index = 0; index < active_config.entries.size(); ++index) {
        const auto& entry = active_config.entries[index];
        descriptors.push_back({entry.key, entry_directions[index], offsets[index],
            static_cast<std::uint8_t>(bit_positions[index]), entry.bits});
    }
    ecmotion::PdoLayout pdo_layout(std::move(descriptors), ecrt_domain_size(domain));
    std::unique_ptr<ecmotion::CyclicAxisTrace> axis_trace;
    if(!axis_trace_path.empty()) {
        if(axis_configurations.size()!=1) fail("axis trace currently requires exactly one configured axis");
        try {axis_trace=std::make_unique<ecmotion::CyclicAxisTrace>(pdo_layout,axis_trace_prefix,axis_trace_capacity);}
        catch(const std::exception& error) {fail(error.what());}
    }
    const auto bound_io = io_configuration.bind(pdo_layout);
    if (bound_io.logical_channel_count == 0U && axis_configurations.empty())
        fail("IO map contains no logical channels and no axes are configured");
    ecmotion::PdoImageView pdo_image(
        std::span<std::uint8_t>(process_data, pdo_layout.image_size_bytes()), pdo_layout);
    for (const auto& entry : active_config.entries) {
        const auto handle = pdo_layout.resolve(entry.key);
        if (pdo_layout.entry(handle).direction == ecmotion::PdoDirection::ControllerOutput) {
            pdo_image.clear_from_controller(handle);
        }
    }
    ecmotion::RealtimeExchange realtime_exchange;
    ecmotion::PdoIoCycle io_cycle(bound_io, pdo_image);
    io_cycle.apply_safe_state();
    std::vector<std::unique_ptr<ecmotion::RealtimeAxisAdapter>> axis_adapters;
    std::vector<ecmotion::RealtimeAxisAdapter*> axis_pointers;
    std::vector<std::string> axis_names;
    std::vector<std::string> axis_position_units;
    axis_adapters.reserve(axis_configurations.size());
    axis_pointers.reserve(axis_configurations.size());
    axis_names.reserve(axis_configurations.size());
    axis_position_units.reserve(axis_configurations.size());
    for (const auto& configuration : axis_configurations) {
        axis_names.push_back(configuration.name);
        axis_position_units.push_back(configuration.position_unit);
        axis_adapters.push_back(ecmotion::make_axis_adapter(pdo_layout, configuration));
        axis_pointers.push_back(axis_adapters.back().get());
    }
    ecmotion::FieldbusCycle fieldbus_cycle(realtime_exchange, io_cycle,
        std::move(axis_pointers), static_cast<double>(active_config.cycle_time_ns) / 1'000'000'000.0);
    ecmotion::FieldbusControlBackend fieldbus_backend(
        realtime_exchange, digital_output_names(io_configuration), std::move(axis_names),
        digital_input_names(io_configuration), std::move(axis_position_units), [&] {
            std::vector<std::string> names;
            for (const auto& group : io_configuration.groups())
                if (group.encoding != ecmotion::LogicalEncoding::PackedBoolean)
                    for (std::size_t i = 0; i < group.channel_count; ++i)
                        names.push_back(group.name_prefix + "." + std::to_string(i));
            return names;
        }(), io_configuration.catalog(pdo_layout, active_config.topology_fingerprint));
    fieldbus_backend.set_ethercat_topology_fingerprint(active_config.topology_fingerprint);
    ecmotion::NativeProtocolServer protocol_server(
        socket_path, fieldbus_backend, "EcMotion.IgH/0.2");

    std::signal(SIGINT, stop_handler);
    std::signal(SIGTERM, stop_handler);
    std::signal(SIGUSR1, trace_handler);
    std::signal(SIGUSR2, trace_handler);
    std::atomic<bool> trace_worker_running{true};
    std::thread trace_thread([&] {
        unsigned sequence=0;bool export_pending=false;
        while(trace_worker_running.load(std::memory_order_acquire)) {
            const auto action=trace_signal.exchange(0,std::memory_order_relaxed);
            if(axis_trace && action==1) {
                if(axis_trace->request_freeze()) export_pending=true;
                else std::fprintf(stderr,"ecmotion trace freeze rejected: rearm pending\n");
            }
            if(axis_trace && export_pending && axis_trace->frozen()) {
                const auto path=axis_trace_path+".capture-"+std::to_string(++sequence)+".csv";
                try {axis_trace->write_csv_exclusive(path);std::fprintf(stderr,"ecmotion trace exported: %s\n",path.c_str());}
                catch(const std::exception& error) {std::fprintf(stderr,"ecmotion trace export failed: %s\n",error.what());}
                export_pending=false;
            }
            if(axis_trace && action==2 && !export_pending) {
                if(!axis_trace->request_rearm()) std::fprintf(stderr,"ecmotion trace rearm rejected: freeze/export first\n");
                else std::fprintf(stderr,"ecmotion trace rearm requested\n");
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
    });
    RuntimeDiagnostics diagnostics(active_config.slaves.size());
    std::thread diagnostic_thread(diagnostics_loop, std::cref(diagnostics));
    std::thread protocol_thread([&protocol_server] {
        try {
            protocol_server.run();
        } catch (const std::exception& error) {
            std::fprintf(stderr, "ecmotion-rt: protocol service failed: %s\n", error.what());
            protocol_failed.store(true, std::memory_order_relaxed);
        }
    });
    configure_realtime(cpu);

    timespec wakeup{};
    if (clock_gettime(CLOCK_MONOTONIC, &wakeup) != 0) fail("clock_gettime failed");
    wakeup = add_nanoseconds(wakeup, active_config.cycle_time_ns);
    bool dc_monitor_pending = false;
    std::uint64_t previous_sent_cycle=0,previous_tx_start_ns=0,previous_scheduled_ns=0;
    bool previous_send_budget_overrun=false;
    while (running != 0 && !protocol_failed.load(std::memory_order_relaxed)) {
        int sleep_result{};
        do {
            sleep_result = clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &wakeup, nullptr);
        } while (sleep_result == EINTR && running != 0);
        if (sleep_result != 0 && sleep_result != EINTR) fail("clock_nanosleep failed");

        timespec actual_wakeup{};
        if (clock_gettime(CLOCK_MONOTONIC, &actual_wakeup) != 0) fail("clock_gettime failed");
        const auto lateness = difference_nanoseconds(actual_wakeup, wakeup);
        update_maximum(diagnostics.maximum_lateness_ns, lateness);
        if (lateness > static_cast<std::int64_t>(active_config.cycle_time_ns)) {
            diagnostics.overruns.fetch_add(1, std::memory_order_relaxed);
        }

        ecrt_master_application_time(master.get(), to_nanoseconds(wakeup));
        ecmotion::CyclicTiming timing;
        timespec observation{};
        if(clock_gettime(CLOCK_MONOTONIC,&observation)!=0) fail("receive timestamp failed");
        timing.rx_start_ns=to_nanoseconds(observation);
        ecrt_master_receive(master.get());
        ecrt_domain_process(domain);
        if(clock_gettime(CLOCK_MONOTONIC,&observation)!=0) fail("received timestamp failed");
        timing.rx_end_ns=to_nanoseconds(observation);
        timing.reference_valid=ecrt_master_reference_clock_time(master.get(),&timing.reference_time_low32)==0 && previous_sent_cycle!=0;
        timing.reference_cycle=previous_sent_cycle;timing.reference_tx_start_ns=previous_tx_start_ns;
        timing.reference_scheduled_ns=previous_scheduled_ns;
        timing.cycle_timing_enabled=delivery_monitor.has_value();
        if(delivery_monitor) {
            if(previous_send_budget_overrun) delivery_monitor->reset();
            timing.delivery=delivery_monitor->observe({
                diagnostics.cycles.load(std::memory_order_relaxed)+1,
                to_nanoseconds(wakeup),timing.rx_end_ns,timing.reference_valid,
                timing.reference_time_low32,previous_sent_cycle,previous_scheduled_ns,previous_tx_start_ns});
            timing.motion_timing_ready=timing.delivery.ready && !previous_send_budget_overrun;
        }
        for(auto& axis:axis_adapters) axis->set_motion_timing_ready(timing.motion_timing_ready);
        ec_domain_state_t cycle_domain_state{};
        ecrt_domain_state(domain, &cycle_domain_state);
        const auto realtime_bus_ready = bus_is_ready(diagnostics)
            && cycle_domain_state.wc_state == EC_WC_COMPLETE;
        fieldbus_cycle.set_bus_ready(realtime_bus_ready);
        fieldbus_backend.set_bus_ready(realtime_bus_ready);
        std::int32_t previous_target=0;
        if(axis_trace) previous_target=axis_trace->target_raw(pdo_image);
        auto cycle_snapshot=fieldbus_cycle.process(pdo_image,to_nanoseconds(actual_wakeup));
        if(clock_gettime(CLOCK_MONOTONIC,&observation)!=0) fail("processed timestamp failed");
        timing.process_end_ns=to_nanoseconds(observation);
        if (dc_monitor_pending) {
            const auto difference = ecrt_master_sync_monitor_process(master.get());
            if (difference != std::numeric_limits<std::uint32_t>::max()) {
                diagnostics.dc_difference_ns.store(difference, std::memory_order_relaxed);
                auto maximum = diagnostics.maximum_dc_difference_ns.load(std::memory_order_relaxed);
                while (difference > maximum
                       && !diagnostics.maximum_dc_difference_ns.compare_exchange_weak(
                           maximum, difference, std::memory_order_relaxed)) {
                }
            }
            dc_monitor_pending = false;
        }

        // Commands are routed through vendor-neutral IO/axis adapters. Motion profiles remain interlocked.

        ecrt_master_sync_reference_clock(master.get());
        ecrt_master_sync_slave_clocks(master.get());
        const auto upcoming_cycle = diagnostics.cycles.load(std::memory_order_relaxed) + 1;
        if (upcoming_cycle % 1000U == 0U
            && ecrt_master_sync_monitor_queue(master.get()) == 0) {
            dc_monitor_pending = true;
        }
        if(clock_gettime(CLOCK_MONOTONIC,&observation)!=0) fail("pre-send timestamp failed");
        timing.pre_send_check_ns=to_nanoseconds(observation);
        if(delivery_monitor) {
            timing.pre_send_budget_overrun=timing.pre_send_check_ns<timing.rx_end_ns ||
                timing.pre_send_check_ns-timing.rx_end_ns>cycle_timing_config->remaining_send_budget_ns;
            timing.send_host_deadline_overrun=timing.pre_send_check_ns<to_nanoseconds(wakeup) ||
                timing.pre_send_check_ns-to_nanoseconds(wakeup)>=active_config.cycle_time_ns;
            if(timing.pre_send_budget_overrun || timing.send_host_deadline_overrun) {
                timing.motion_timing_ready=false;
                delivery_monitor->reset();
                // Rewrite only axes. A second FieldbusCycle would dequeue commands,
                // advance groups and IO, and publish an extra logical cycle.
                for(std::size_t i=0;i<axis_adapters.size();++i) {
                    axis_adapters[i]->set_motion_timing_ready(false);
                    axis_adapters[i]->process(pdo_image,cycle_snapshot.axes[i]);
                }
                const auto& first=cycle_snapshot.axes[0];
                cycle_snapshot.state=first.state;cycle_snapshot.homed=first.homed;
                cycle_snapshot.actual_position=first.actual_position;
                cycle_snapshot.actual_velocity=first.actual_velocity;
                cycle_snapshot.statusword=first.statusword;cycle_snapshot.mode_display=first.mode_display;
                cycle_snapshot.error_code=first.error_code;
                cycle_snapshot.commissioning_required=first.commissioning_required;
                cycle_snapshot.software_limit_violation=first.software_limit_violation;
                cycle_snapshot.following_error_fault=first.following_error_fault;
                cycle_snapshot.overspeed_fault=first.overspeed_fault;
                if(cycle_snapshot.path_status==1) cycle_snapshot.path_status=3;
                realtime_exchange.publish(cycle_snapshot);
            }
        }
        // One queue only. Queue/send overhead is measured, not a guaranteed wire deadline.
        ecrt_domain_queue(domain);
        if(clock_gettime(CLOCK_MONOTONIC,&observation)!=0) fail("send timestamp failed");
        timing.tx_start_ns=to_nanoseconds(observation);
        if(delivery_monitor) {
            timing.actual_send_budget_overrun=timing.tx_start_ns<timing.rx_end_ns ||
                timing.tx_start_ns-timing.rx_end_ns>cycle_timing_config->remaining_send_budget_ns;
            timing.send_host_deadline_overrun=timing.send_host_deadline_overrun ||
                timing.tx_start_ns<to_nanoseconds(wakeup) ||
                timing.tx_start_ns-to_nanoseconds(wakeup)>=active_config.cycle_time_ns;
        }
        ecrt_master_send(master.get());
        timespec cycle_finished{};
        if (clock_gettime(CLOCK_MONOTONIC, &cycle_finished) != 0) fail("clock_gettime failed");
        timing.tx_end_ns=to_nanoseconds(cycle_finished);
        if(delivery_monitor) {
            timing.actual_send_budget_overrun=timing.actual_send_budget_overrun ||
                timing.tx_end_ns<timing.rx_end_ns ||
                timing.tx_end_ns-timing.rx_end_ns>cycle_timing_config->remaining_send_budget_ns;
            timing.send_host_deadline_overrun=timing.send_host_deadline_overrun ||
                timing.tx_end_ns<to_nanoseconds(wakeup) ||
                timing.tx_end_ns-to_nanoseconds(wakeup)>=active_config.cycle_time_ns;
        }
        if(axis_trace) axis_trace->capture(pdo_image,cycle_snapshot.axes[0],
            diagnostics.cycles.load(std::memory_order_relaxed)+1,
            to_nanoseconds(wakeup),to_nanoseconds(actual_wakeup),realtime_bus_ready,previous_target,timing);
        previous_tx_start_ns=timing.tx_start_ns;
        previous_sent_cycle=diagnostics.cycles.load(std::memory_order_relaxed)+1;
        previous_scheduled_ns=to_nanoseconds(wakeup);
        previous_send_budget_overrun=timing.pre_send_budget_overrun ||
            timing.actual_send_budget_overrun || timing.send_host_deadline_overrun;
        if(clock_gettime(CLOCK_MONOTONIC,&cycle_finished)!=0) fail("cycle finish timestamp failed");
        const auto execution_ns = difference_nanoseconds(cycle_finished, actual_wakeup);
        diagnostics.last_execution_ns.store(execution_ns, std::memory_order_relaxed);
        update_maximum(diagnostics.maximum_execution_ns, execution_ns);
        if (execution_ns > static_cast<std::int64_t>(active_config.cycle_time_ns))
            diagnostics.execution_overruns.fetch_add(1, std::memory_order_relaxed);
        const auto cycle = diagnostics.cycles.fetch_add(1, std::memory_order_relaxed) + 1;
        if (cycle % 100U == 0U) {
            sample_bus_state(master.get(), domain, slave_configs, diagnostics);
        }
        wakeup = add_nanoseconds(wakeup, active_config.cycle_time_ns);
        // Skip missed release slots on the existing grid; never run catch-up bursts.
        if(clock_gettime(CLOCK_MONOTONIC,&cycle_finished)!=0) fail("reschedule timestamp failed");
        const auto finished_ns=to_nanoseconds(cycle_finished);
        const auto next_ns=to_nanoseconds(wakeup);
        if(next_ns<=finished_ns) {
            const auto skipped=(finished_ns-next_ns)/active_config.cycle_time_ns+1;
            if(skipped>(std::numeric_limits<std::uint64_t>::max()-next_ns)/active_config.cycle_time_ns)
                fail("next cycle timestamp overflow");
            const auto future_ns=next_ns+skipped*active_config.cycle_time_ns;
            wakeup.tv_sec=static_cast<time_t>(future_ns/1000000000ULL);
            wakeup.tv_nsec=static_cast<long>(future_ns%1000000000ULL);
        }
    }

    running = 0;
    protocol_server.request_stop();
    protocol_thread.join();
    diagnostic_thread.join();
    trace_worker_running.store(false,std::memory_order_release);
    trace_thread.join();
    if(axis_trace) {
        sched_param ordinary{};
        if(sched_setscheduler(0,SCHED_OTHER,&ordinary)!=0) fail("leave realtime scheduling before trace export failed");
        try {axis_trace->write_csv_exclusive(axis_trace_path);}
        catch(const std::exception& error) {fail(error.what());}
        std::fprintf(stderr,"ecmotion axis trace: rows=%zu dropped=%llu\n",
            axis_trace->frames().size(),static_cast<unsigned long long>(axis_trace->dropped()));
    }
    return protocol_failed.load(std::memory_order_relaxed) ? EXIT_FAILURE : EXIT_SUCCESS;
}

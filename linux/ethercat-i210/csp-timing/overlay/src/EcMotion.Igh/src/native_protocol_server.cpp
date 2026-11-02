#include "native_protocol_server.hpp"
#include "motion_runtime_version.hpp"
#include <cmath>

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <cerrno>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <ctime>
#include <iomanip>
#include <limits>
#include <mutex>
#include <optional>
#include <random>
#include <sstream>
#include <stdexcept>
#include <string_view>
#include <thread>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

namespace ecmotion {
namespace {
using json = nlohmann::json;
using Clock = std::chrono::system_clock;
constexpr std::uint32_t maximum_frame_bytes = 1024U * 1024U;
constexpr int protocol_version = 4;

struct ProtocolError final : std::runtime_error {
    ProtocolError(std::string code_value, std::string message, bool retryable_value = false)
        : std::runtime_error(std::move(message)), code(std::move(code_value)), retryable(retryable_value) {}
    std::string code;
    bool retryable;
};

struct LeaseState {
    std::mutex mutex;
    std::uint64_t scan_session{};
    std::uint64_t scan_index{};
    std::uint64_t action_watermark{};
    std::string token;
    int owner{-1};
    int duration_ms{};
    Clock::time_point expires_at{};
    std::uint64_t last_sequence{};
    std::string last_request_id;
    std::optional<json> last_response;
};

LeaseState lease;

bool read_exact(int descriptor, void* destination, std::size_t size) {
    auto* output = static_cast<std::byte*>(destination);
    std::size_t total = 0;
    while (total < size) {
        const auto count = ::recv(descriptor, output + total, size - total, 0);
        if (count == 0) return false;
        if (count < 0) {
            if (errno == EINTR) continue;
            throw std::runtime_error("socket read failed: " + std::string(std::strerror(errno)));
        }
        total += static_cast<std::size_t>(count);
    }
    return true;
}

void write_exact(int descriptor, const void* source, std::size_t size) {
    const auto* input = static_cast<const std::byte*>(source);
    std::size_t total = 0;
    while (total < size) {
        const auto count = ::send(descriptor, input + total, size - total, MSG_NOSIGNAL);
        if (count < 0) {
            if (errno == EINTR) continue;
            throw std::runtime_error("socket write failed: " + std::string(std::strerror(errno)));
        }
        total += static_cast<std::size_t>(count);
    }
}

std::optional<json> read_frame(int descriptor) {
    std::array<std::uint8_t, 4> header{};
    if (!read_exact(descriptor, header.data(), header.size())) return std::nullopt;
    const auto size = static_cast<std::uint32_t>(header[0])
        | (static_cast<std::uint32_t>(header[1]) << 8U)
        | (static_cast<std::uint32_t>(header[2]) << 16U)
        | (static_cast<std::uint32_t>(header[3]) << 24U);
    if (size == 0U || size > maximum_frame_bytes) {
        throw ProtocolError("invalid_request", "Invalid frame length.");
    }
    std::string payload(size, '\0');
    if (!read_exact(descriptor, payload.data(), payload.size())) {
        throw std::runtime_error("socket ended inside frame payload");
    }
    return json::parse(payload);
}

void write_frame(int descriptor, const json& value) {
    const auto payload = value.dump();
    if (payload.size() > maximum_frame_bytes) throw std::runtime_error("response frame is too large");
    const auto size = static_cast<std::uint32_t>(payload.size());
    const std::array<std::uint8_t, 4> header{
        static_cast<std::uint8_t>(size & 0xFFU),
        static_cast<std::uint8_t>((size >> 8U) & 0xFFU),
        static_cast<std::uint8_t>((size >> 16U) & 0xFFU),
        static_cast<std::uint8_t>((size >> 24U) & 0xFFU)};
    write_exact(descriptor, header.data(), header.size());
    write_exact(descriptor, payload.data(), payload.size());
}

std::string make_token() {
    std::random_device random;
    std::uniform_int_distribution<unsigned int> byte(0U, 255U);
    std::array<unsigned int, 16> bytes{};
    for (auto& value : bytes) value = byte(random);
    bytes[6] = (bytes[6] & 0x0FU) | 0x40U;
    bytes[8] = (bytes[8] & 0x3FU) | 0x80U;
    std::ostringstream output;
    output << std::hex << std::setfill('0');
    for (std::size_t index = 0; index < bytes.size(); ++index) {
        if (index == 4U || index == 6U || index == 8U || index == 10U) output << '-';
        output << std::setw(2) << bytes[index];
    }
    return output.str();
}

std::string format_timestamp(Clock::time_point value) {
    const auto milliseconds = std::chrono::duration_cast<std::chrono::milliseconds>(
        value.time_since_epoch()) % 1000;
    const auto time = Clock::to_time_t(value);
    std::tm utc{};
    gmtime_r(&time, &utc);
    std::ostringstream output;
    output << std::put_time(&utc, "%Y-%m-%dT%H:%M:%S") << '.'
           << std::setw(3) << std::setfill('0') << milliseconds.count() << 'Z';
    return output.str();
}

json response(const json& request, std::string_view kind, json payload) {
    return {{"protocolVersion", protocol_version}, {"kind", kind},
        {"requestId", request.at("requestId")}, {"sequence", request.at("sequence")},
        {"payload", std::move(payload)}};
}

json error_response(const json& request, const ProtocolError& error) {
    return response(request, "ErrorResponse",
        {{"code", error.code}, {"message", error.what()}, {"retryable", error.retryable}});
}

void clear_expired_lease(ControlBackend& backend) {
    std::scoped_lock lock(lease.mutex);
    if (!lease.token.empty() && lease.expires_at <= Clock::now()) {
        lease.token.clear();
        lease.owner = -1;
        lease.last_sequence = 0U;
        lease.last_request_id.clear();
        lease.last_response.reset();
        backend.apply_safe_state();
    }
}

bool is_leased_command(std::string_view kind) {
    return kind == "HeartbeatRequest" || kind == "ReleaseControlRequest"
        || kind == "WriteOutputsRequest" || kind == "AxisCommandRequest"
        || kind == "GroupMotionRequest" || kind == "PlcScanBatchRequest"
        || kind == "DownloadPathRequest" || kind == "StartPathRequest";
}

std::optional<json> begin_lease_command(const json& request, int client) {
    const auto sequence = request.at("sequence").get<std::uint64_t>();
    const auto request_id = request.at("requestId").get<std::string>();
    std::scoped_lock lock(lease.mutex);
    if (lease.owner != client || lease.token.empty()) {
        throw ProtocolError("invalid_lease", "The control lease is no longer active.");
    }
    if (sequence < lease.last_sequence) {
        throw ProtocolError("sequence_conflict", "Command sequence is older than the last accepted sequence.");
    }
    if (sequence == lease.last_sequence) {
        if (request_id == lease.last_request_id && lease.last_response.has_value()) {
            return lease.last_response;
        }
        throw ProtocolError("sequence_conflict", "Command sequence was already used by another request.");
    }
    lease.last_sequence = sequence;
    lease.last_request_id = request_id;
    lease.last_response.reset();
    return std::nullopt;
}

void complete_lease_command(const json& request, int client, const json& reply) {
    const auto sequence = request.at("sequence").get<std::uint64_t>();
    const auto request_id = request.at("requestId").get<std::string>();
    std::scoped_lock lock(lease.mutex);
    if (lease.owner == client && lease.last_sequence == sequence
        && lease.last_request_id == request_id) {
        lease.last_response = reply;
    }
}

void require_lease(const json& payload, int client, ControlBackend& backend) {
    clear_expired_lease(backend);
    const auto token = payload.at("leaseToken").get<std::string>();
    std::scoped_lock lock(lease.mutex);
    if (lease.token.empty() || lease.token != token || lease.owner != client) {
        throw ProtocolError("invalid_lease", "The control lease is missing, expired, or owned by another client.");
    }
}

json dispatch(
    const json& request, int client, ControlBackend& backend, const std::string& controller_version, PathResourceStore& paths) {
    if (request.at("protocolVersion").get<int>() != protocol_version) {
        throw ProtocolError("invalid_request", "Unsupported protocol version.");
    }
    const auto request_id = request.at("requestId").get<std::string>();
    if (request_id.empty() || request_id == "00000000-0000-0000-0000-000000000000") {
        throw ProtocolError("invalid_request", "RequestId cannot be empty.");
    }
    const auto kind = request.at("kind").get<std::string>();
    const auto& payload = request.at("payload");

    const auto profile = [&](const json& input, RealtimeCommand& c) {
        if (!input.contains("motionProfileVersion")) return;
        if (!input.at("motionProfileVersion").is_number_integer()
            || input.at("motionProfileVersion").get<int>() != 2)
            throw ProtocolError("invalid_argument", "Unsupported motion profile version.");
        if ((c.kind == RealtimeCommandKind::MoveAbsolute && !backend.supports_axis_move_profile_v2())
            || ((c.kind == RealtimeCommandKind::Jog || c.kind == RealtimeCommandKind::Stop) && !backend.supports_axis_velocity_profile_v2())
            || (c.kind != RealtimeCommandKind::MoveAbsolute && c.kind != RealtimeCommandKind::Jog && c.kind != RealtimeCommandKind::Stop))
            throw ProtocolError("unsupported_capability", "Axis command profile v2 is not supported.");
        c.motion_profile_version = 2;
        c.acceleration = input.value("acceleration", 0.0);
        c.deceleration = input.value("deceleration", 0.0);
        c.jerk = input.value("jerk", 0.0);
        for (double v : {c.acceleration,c.deceleration,c.jerk})
            if (!std::isfinite(v) || v < 0.0)
                throw ProtocolError("invalid_argument", "Axis profile values must be finite and nonnegative.");
    };
    if (kind == "HelloRequest") {
        auto capabilities = backend.supports_motion_migration()
            ? json::array({motion_migration_capability,axis_trace_capability}) : json::array();
        if (backend.supports_axis_move_profile_v2()) capabilities.push_back("workbench_axis_move_profile_v2");
        if (backend.supports_axis_velocity_profile_v2()) capabilities.push_back("workbench_axis_velocity_profile_v2");
        static_cast<void>(payload.at("clientName").get<std::string>());
        return response(request, "HelloResponse", {{"controllerVersion", controller_version},
            {"protocolVersion", protocol_version}, {"runtimeReady", backend.runtime_ready()},
            {"motionReady", backend.motion_ready()},
            {"ioReady", backend.io_ready()}, {"ethercatTopologyFingerprint",backend.ethercat_topology_fingerprint()}, {"plcScanBatch", true}, {"plcAbiVersion", 3},
            {"plcActionReceipts", true}, {"plcBlockAbiVersions", json::array({4})},
            {"runtimeCapabilities", capabilities},
            {"plcTypedIo", !backend.io_catalog().fingerprint.empty()}, {"pathResources",true},
            {"autonomousPaths",backend.supports_paths()}});
    }
    if (kind=="ListPathsRequest") return response(request,"PathListResponse",{{"paths",paths.list()}});
    if (kind=="ReadPathRequest") return response(request,"PathResourceResponse",PathResourceStore::describe(
        paths.get(payload.at("id").get<std::uint64_t>(),payload.at("version").get<std::uint64_t>()),true));
    if (kind == "ReadIoCatalogRequest") {
        const auto catalog = backend.io_catalog();
        if (catalog.fingerprint.empty()) throw ProtocolError("unsupported", "Typed IO catalog is not available.");
        auto channels = json::array();
        for (const auto& c : catalog.channels)
            channels.push_back({{"name", c.name}, {"type", c.type}, {"pdoEntry", c.pdo_entry},
                {"pdoBit", c.pdo_bit}, {"pdoWidth", c.pdo_width}, {"output", c.output},
                {"commissioningRequired", c.commissioning_required}, {"safeBits", c.safe_bits}});
        return response(request, "IoCatalogResponse", {{"schemaVersion", 1}, {"hardwareFingerprint", catalog.fingerprint},
            {"channels", channels}});
    }
    if (kind == "AcquireControlRequest") {
        static_cast<void>(payload.at("clientName").get<std::string>());
        const auto requested = payload.at("requestedLeaseMilliseconds").get<int>();
        if (requested <= 0) throw ProtocolError("invalid_argument", "Lease duration must be positive.");
        clear_expired_lease(backend);
        std::scoped_lock lock(lease.mutex);
        if (!lease.token.empty()) throw ProtocolError("control_busy", "Another client owns the control lease.", true);
        lease.duration_ms = std::clamp(requested, 250, 30'000);
        lease.expires_at = Clock::now() + std::chrono::milliseconds(lease.duration_ms);
        lease.token = make_token();
        lease.owner = client;
        lease.scan_session = lease.scan_index = 0;
        lease.action_watermark = 0;
        lease.last_sequence = 0U;
        lease.last_request_id.clear();
        lease.last_response.reset();
        return response(request, "AcquireControlResponse", {{"leaseToken", lease.token},
            {"grantedLeaseMilliseconds", lease.duration_ms},
            {"expiresAt", format_timestamp(lease.expires_at)}});
    }
    if (kind == "ReadSnapshotRequest") {
        clear_expired_lease(backend);
        backend.begin_snapshot_read();
        struct SnapshotGuard {
            const ControlBackend& backend;
            ~SnapshotGuard() { backend.end_snapshot_read(); }
        } snapshot_guard{backend};
        const auto include_diagnostics = payload.at("includeDiagnostics").get<bool>();
        for(int attempt=0;attempt<8;++attempt) {
        const auto [sample_cycle, sample_monotonic_ns] = backend.sample_stamp();
        const auto [applied_sequence, applied_cycle] = backend.applied_command_stamp();
        json axes = json::array();
        for (const auto& name : backend.axis_names()) {
            const auto axis = backend.axis_snapshot(name);
            axes.push_back({{"name", axis.name}, {"state", axis.state},
                {"driveStatusword",axis.drive_statusword_valid ? json(axis.drive_statusword) : json(nullptr)},
                {"actualPosition", axis.actual_position}, {"actualVelocity", axis.actual_velocity},
                {"homed", axis.homed}, {"fault", axis.fault.empty() ? json(nullptr) : json(axis.fault)},
                {"motionId", axis.motion_id}, {"motionTarget", axis.motion_target},
                {"motionBusy", axis.motion_busy}, {"motionDone", axis.motion_done},
                {"motionAborted", axis.motion_aborted},{"cycle_target", axis.cycle_target},{"cycle_error", axis.cycle_error},{"peak_error", axis.peak_error},{"max_excess_ms", axis.max_excess_ms},{"feedforward_velocity", axis.feedforward_velocity},{"peak_actual_velocity", axis.peak_actual_velocity},{"demandPositionError",axis.demand_position_error},{"driveFollowingError",axis.drive_following_error},
                {"demandDeliveryError",axis.demand_delivery_error},{"demandFeedbackValid",axis.demand_feedback_valid},
                {"demandDeliveryMismatch",axis.demand_delivery_mismatch},{"demandDeliveryFault",axis.demand_delivery_fault},
                {"timingReady",axis.motion_timing_ready},{"timingFault",axis.motion_timing_fault}});
        }
        const auto outputs=backend.digital_outputs(), inputs=backend.digital_inputs();
        const auto outputs_safe = backend.outputs_at_safe_state();
        json scalar_io = json::array();
        for (const auto& value : backend.scalar_io())
            scalar_io.push_back({{"name", value.name}, {"rawBits", value.raw_bits},
                {"value", std::isfinite(value.engineering_value) ? json(value.engineering_value) : json(nullptr)},
                {"quality", value.quality == 0 ? "Good" : value.quality == 1 ? "CommissioningRequired" : "InvalidRaw"},
                {"type", value.encoding == 1 ? "INT" : "REAL"},
                {"direction", value.output ? "Output" : "Input"}});
        const auto ready=backend.runtime_ready(), io_ready=backend.io_ready();
        const auto path_execution=backend.path_execution();
        if(backend.sample_stamp()!=std::pair{sample_cycle,sample_monotonic_ns}) continue;
        return response(request, "SnapshotResponse", {{"timestamp", format_timestamp(Clock::now())},
            {"sampleCycle", sample_cycle}, {"sampleMonotonicNs", sample_monotonic_ns},
            {"lastAppliedCommandSequence", applied_sequence},
            {"lastAppliedCommandCycle", applied_cycle},
            {"runtimeReady", ready}, {"motionReady", backend.motion_ready()}, {"ioReady", io_ready},
            {"axes", std::move(axes)},
            {"digitalOutputs", outputs}, {"outputFeedbackAvailable", false},
            {"outputsAtConfiguredSafeState", outputs_safe},
            {"digitalInputs", inputs},
            {"scalarIo", std::move(scalar_io)},
            {"pathExecutionId",path_execution.id},{"pathCompletedPoints",path_execution.completed},
            {"pathTotalPoints",path_execution.total},{"pathStatus",path_execution.status},
            {"diagnostics", include_diagnostics ? json(backend.diagnostics()) : json(nullptr)}});
        }
        throw ProtocolError("snapshot_busy", "Consistent snapshot temporarily unavailable.", true);
    }

    require_lease(payload, client, backend);
    if (const auto replay = begin_lease_command(request, client); replay.has_value()) return *replay;
    if (kind=="DownloadPathRequest") {
        const auto resource=paths.download(payload.at("resource"),backend.axis_names());
        return response(request,"PathResourceResponse",PathResourceStore::describe(resource,true));
    }
    if (kind=="StartPathRequest") {
        const auto resource=paths.get(payload.at("id").get<std::uint64_t>(),payload.at("version").get<std::uint64_t>());
        const auto execution=backend.start_path(resource.path,resource.axes);
        return response(request,"CommandResponse",{{"accepted",true},{"motionId",execution},
            {"message","ExactStop path accepted; completion must be confirmed by feedback."}});
    }
    if (kind == "HeartbeatRequest") {
        std::scoped_lock lock(lease.mutex);
        lease.expires_at = Clock::now() + std::chrono::milliseconds(lease.duration_ms);
        return response(request, "CommandResponse",
            {{"accepted", true}, {"message", "Lease renewed until " + format_timestamp(lease.expires_at) + "."}});
    }
    if (kind == "ReleaseControlRequest") {
        backend.apply_safe_state();
        std::scoped_lock lock(lease.mutex);
        lease.token.clear();
        lease.owner = -1;
        lease.last_sequence = 0U;
        lease.last_request_id.clear();
        lease.last_response.reset();
        return response(request, "CommandResponse", {{"accepted", true}, {"message", "Control released."}});
    }
    if (kind == "PlcScanBatchRequest") {
        if(!backend.runtime_ready()) throw ProtocolError("invalid_state","PLC runtime is not ready.");
        const auto session=payload.at("sessionId").get<std::uint64_t>();
        const auto scan=payload.at("scanIndex").get<std::uint64_t>();
        const auto deadline=payload.at("deadlineNs").get<std::uint64_t>();
        const auto now=static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count());
        const auto source=payload.at("sourceCycle").get<std::uint64_t>();
        const auto stamp=backend.sample_stamp();
        if(!session || !scan || !source || source>stamp.first || deadline<=now || deadline-now>100'000'000)
            throw ProtocolError("invalid_argument","Invalid or expired scan batch stamp.");
        { std::scoped_lock lock(lease.mutex);
          if((lease.scan_session && lease.scan_session!=session) || scan<=lease.scan_index)
              throw ProtocolError("sequence_conflict","Scan batch was replayed or belongs to another session.");
          lease.scan_session=session; lease.scan_index=scan; }
        const auto axes=payload.at("axes").get<std::vector<std::string>>();
        const auto outputs=payload.at("outputs").get<std::vector<std::string>>();
        const auto& actions=payload.at("actions");
        std::vector<IoRawWrite> io_writes;
        if (payload.contains("ioWrites")) {
            const auto catalog = backend.io_catalog();
            if (catalog.fingerprint.empty() || payload.value("hardwareFingerprint", "") != catalog.fingerprint)
                throw ProtocolError("configuration_mismatch", "PLC and controller hardware configurations differ.");
            const auto& writes = payload.at("ioWrites");
            if (!writes.is_array() || writes.size() > 512) throw ProtocolError("invalid_argument", "Typed IO capacity exceeded.");
            for (const auto& write : writes)
                io_writes.push_back({write.at("name").get<std::string>(), write.at("rawBits").get<std::uint32_t>()});
        }
        if(axes.size()>256 || outputs.size()>64 || !actions.is_array() || (actions.empty() && io_writes.empty()) || actions.size()>16)
            throw ProtocolError("invalid_argument","Invalid scan batch capacity.");
        std::vector<RealtimeCommand> commands;
        std::uint64_t last_id{};
        { std::scoped_lock lock(lease.mutex); last_id = lease.action_watermark; }
        if (actions.size()==1 && actions[0].at("kind").get<int>()==10) {
            const auto& a=actions[0];
            const auto id=a.at("requestId").get<std::uint64_t>();
            if (id<=last_id || !io_writes.empty()) throw ProtocolError("invalid_argument","Path request requires one new action and no IO writes.");
            const auto resource=paths.get(a.at("outputMask").get<std::uint64_t>(),a.at("outputValues").get<std::uint64_t>());
            const auto mapped=a.at("groupAxes").get<std::array<std::uint16_t,4>>();
            if (a.at("groupCount").get<std::uint8_t>()!=resource.axes.size())
                throw ProtocolError("configuration_mismatch","Path axis count differs from program reference.");
            for (std::size_t i=0;i<resource.axes.size();++i)
                if (mapped[i]>=axes.size() || axes[mapped[i]]!=resource.axes[i])
                    throw ProtocolError("configuration_mismatch","Path axis mapping differs from program reference.");
            const auto motion=backend.start_path(resource.path,resource.axes);
            { std::scoped_lock lock(lease.mutex); lease.action_watermark=id; }
            const auto applied=backend.applied_command_stamp();
            return response(request,"CommandResponse",{{"accepted",true},{"motionId",motion},{"appliedCycle",applied.second},
                {"actionReceipts",json::array({{{"requestId",id},{"motionId",motion},{"accepted",true},{"appliedCycle",applied.second}}})}});
        }
        for(const auto& a:actions) {
            RealtimeCommand c;
            const auto id=a.at("requestId").get<std::uint64_t>();
            if(id<=last_id) throw ProtocolError("invalid_argument","Action IDs must increase within a batch.");
            last_id=id;
            const auto kind=a.at("kind").get<int>();
            switch(kind) {
                case 1: c.kind=RealtimeCommandKind::WriteOutputs; break;
                case 2: c.kind=RealtimeCommandKind::Enable; break;
                case 3: c.kind=RealtimeCommandKind::Home; break;
                case 4: c.kind=RealtimeCommandKind::MoveAbsolute; break;
                case 5: c.kind=RealtimeCommandKind::Stop; break;
                case 6: c.kind=RealtimeCommandKind::GroupLine; break;
                case 7: c.kind=RealtimeCommandKind::GroupArc; break;
                case 8: c.kind=RealtimeCommandKind::GroupSpatialLine; break;
                case 9: c.kind=RealtimeCommandKind::GroupSpatialArc; break;
                case 11: c.kind=RealtimeCommandKind::Jog; break;
                default: throw ProtocolError("invalid_argument","Unknown scan action.");
            }
            c.axis_index=a.at("axis").get<std::uint16_t>();
            c.output_mask=a.at("outputMask").get<std::uint64_t>();
            c.output_values=a.at("outputValues").get<std::uint64_t>();
            c.position=a.at("position").get<double>(); c.velocity=a.at("velocity").get<double>();
            c.acceleration=a.at("acceleration").get<double>(); c.deceleration=a.at("deceleration").get<double>();
            c.group_axis_count=a.at("groupCount").get<std::uint8_t>();
            if (kind == 1 && (c.output_mask == 0 || (c.output_values & ~c.output_mask) != 0))
                throw ProtocolError("invalid_argument", "Invalid digital output batch.");
            if (kind == 4 && (!std::isfinite(c.position) || !std::isfinite(c.velocity) || c.velocity <= 0))
                throw ProtocolError("invalid_argument", "Invalid motion target.");
            if (kind >= 6 && kind <= 9 && ((kind <= 7 ? c.group_axis_count != 2 : (c.group_axis_count < 3 || c.group_axis_count > 4))
                || !std::isfinite(c.velocity) || c.velocity <= 0
                || !std::isfinite(c.acceleration) || c.acceleration <= 0
                || !std::isfinite(c.deceleration) || c.deceleration <= 0))
                throw ProtocolError("invalid_argument", "Invalid group motion batch.");
            if(kind>=6 && kind<=9) {
                const auto ids=a.at("groupAxes").get<std::array<std::uint16_t,4>>();
                const auto targets=a.at("groupTargets").get<std::array<double,4>>();
                const auto center=a.at("center").get<std::array<double,3>>();
                const auto normal=a.at("normal").get<std::array<double,3>>();
                c.axis_index=ids[0]; c.secondary_axis_index=ids[1]; c.third_axis_index=ids[2]; c.fourth_axis_index=ids[3];
                c.position=targets[0]; c.secondary_position=targets[1]; c.third_position=targets[2]; c.fourth_position=targets[3];
                c.center_x=center[0]; c.center_y=center[1]; c.center_z=center[2];
                c.normal_x=normal[0]; c.normal_y=normal[1]; c.normal_z=normal[2]; c.clockwise=a.at("clockwise").get<bool>();
            }
            profile(a,c);
            commands.push_back(c);
        }
        const auto motion=backend.scan_batch_with_io(commands,axes,outputs,io_writes,deadline);
        { std::scoped_lock lock(lease.mutex); lease.action_watermark = last_id; }
        const auto applied=backend.applied_command_stamp();
        auto receipts = json::array();
        for (std::size_t i = 0; i < commands.size(); ++i) {
            const auto& command = commands[i];
            std::uint64_t motion_id{};
            if (command.kind == RealtimeCommandKind::MoveAbsolute || command.kind == RealtimeCommandKind::GroupLine
                || command.kind == RealtimeCommandKind::GroupArc || command.kind == RealtimeCommandKind::GroupSpatialLine
                || command.kind == RealtimeCommandKind::GroupSpatialArc)
                motion_id = backend.axis_snapshot(axes.at(command.axis_index)).motion_id;
            receipts.push_back({{"requestId", actions[i].at("requestId")}, {"accepted", true},
                {"motionId", motion_id}, {"appliedCycle", applied.second}});
        }
        return response(request,"CommandResponse",{{"accepted",true},{"motionId",motion},
            {"appliedCycle",applied.second},{"scanIndex",scan},{"sessionId",session},{"actionReceipts",receipts}});
    }
    if (kind == "WriteOutputsRequest") {
        if (!backend.io_ready()) {
            throw ProtocolError("invalid_state", "IO bus is not ready for output commands.");
        }
        std::map<std::string, bool> values;
        for (const auto& [name, value] : payload.at("values").items()) {
            const auto number = value.get<double>();
            if (number != 0.0 && number != 1.0) {
                throw ProtocolError("invalid_argument", "Digital outputs must be 0 or 1.");
            }
            values.emplace(name, number == 1.0);
        }
        backend.write_outputs(values);
        return response(request, "CommandResponse", {{"accepted", true}, {"message", nullptr}});
    }
    if (kind == "AxisCommandRequest") {
        const auto command = payload.at("command").get<std::string>();
        const auto recovery_command = command == "Reset" || command == "Disable" || command == "Stop";
        if (!recovery_command && !backend.runtime_ready()) {
            throw ProtocolError("invalid_state", "Runtime is not ready for motion commands.");
        }
        const auto axis = payload.at("axis").get<std::string>();
        if (payload.contains("motionProfileVersion")) {
            RealtimeCommand c;
            if (command == "MoveAbsolute") c.kind = RealtimeCommandKind::MoveAbsolute;
            else if (command == "Jog") c.kind = RealtimeCommandKind::Jog;
            else if (command == "Stop") c.kind = RealtimeCommandKind::Stop;
            else throw ProtocolError("invalid_argument", "Profile on unsupported axis command.");
            c.position = payload.value("position",0.0);
            c.velocity = payload.value("velocity",0.0);
            profile(payload,c);
            const auto motion = backend.axis_profile_command(axis,c);
            return response(request,"CommandResponse",{{"accepted",true},{"message",nullptr},{"motionId",motion}});
        }
        if (command == "Enable") backend.enable(axis);
        else if (command == "Disable") backend.disable(axis);
        else if (command == "Reset") backend.reset(axis);
        else if (command == "Home") backend.home(axis);
        else if (command == "MoveAbsolute") {
            const auto motion_id = backend.move_absolute(
                axis, payload.at("position").get<double>(), payload.at("velocity").get<double>());
            return response(request, "CommandResponse",
                {{"accepted", true}, {"message", nullptr}, {"motionId", motion_id}});
        } else if (command == "Jog") backend.jog(axis, payload.at("velocity").get<double>());
        else if (command == "Stop") backend.stop(axis);
        else throw ProtocolError("invalid_argument", "Unknown axis command.");
        return response(request, "CommandResponse", {{"accepted", true}, {"message", nullptr}});
    }
    if (kind == "GroupMotionRequest") {
        if (!backend.runtime_ready())
            throw ProtocolError("invalid_state", "Runtime is not ready for group motion.");
        const auto motion_kind = payload.at("motionKind").get<std::string>();
        if (motion_kind != "Line" && motion_kind != "Arc"
            && motion_kind != "SpatialLine" && motion_kind != "SpatialArc")
            throw ProtocolError("invalid_argument", "Unknown group motion kind.");
        const GroupMotionCommand command{
            motion_kind == "Arc" ? GroupMotionKind::Arc
                : motion_kind == "SpatialLine" ? GroupMotionKind::SpatialLine
                : motion_kind == "SpatialArc" ? GroupMotionKind::SpatialArc : GroupMotionKind::Line,
            payload.at("xAxis").get<std::string>(),
            payload.at("yAxis").get<std::string>(),
            payload.at("xTarget").get<double>(),
            payload.at("yTarget").get<double>(),
            payload.at("velocity").get<double>(),
            payload.at("acceleration").get<double>(),
            payload.at("deceleration").get<double>(),
            payload.value("centerX", 0.0),
            payload.value("centerY", 0.0),
            payload.value("clockwise", false),
            payload.value("zAxis", std::string{}),
            payload.value("fourthAxis", std::string{}),
            payload.value("zTarget", 0.0),
            payload.value("fourthTarget", 0.0),
            payload.value("centerZ", 0.0),
            payload.value("normalX", 0.0),
            payload.value("normalY", 0.0),
            payload.value("normalZ", 1.0)};
        const auto motion_id = backend.move_group(command);
        return response(request, "CommandResponse",
            {{"accepted", true}, {"message", nullptr}, {"motionId", motion_id}});
    }
    throw ProtocolError("invalid_request", "Message kind is not a supported request.");
}

void release_client_lease(int client, ControlBackend& backend) noexcept {
    std::scoped_lock lock(lease.mutex);
    if (lease.owner == client) {
        lease.token.clear();
        lease.owner = -1;
        lease.last_sequence = 0U;
        lease.last_request_id.clear();
        lease.last_response.reset();
        backend.apply_safe_state();
    }
}
} // namespace

NativeProtocolServer::NativeProtocolServer(
    std::string socket_path, ControlBackend& backend, std::string controller_version)
    : socket_path_(std::move(socket_path)), controller_version_(std::move(controller_version)),
      backend_(backend) {
    if (socket_path_.empty()) throw std::invalid_argument("socket path cannot be empty");
    if (controller_version_.empty()) throw std::invalid_argument("controller version cannot be empty");
    if (socket_path_.size() >= sizeof(sockaddr_un::sun_path)) throw std::invalid_argument("socket path is too long");
}

NativeProtocolServer::~NativeProtocolServer() {
    request_stop();
    ::unlink(socket_path_.c_str());
}

void NativeProtocolServer::run() {
    if (::access(socket_path_.c_str(), F_OK) == 0) {
        throw std::runtime_error("socket path already exists; remove only a confirmed stale socket: " + socket_path_);
    }
    const auto listener = ::socket(AF_UNIX, SOCK_STREAM, 0);
    if (listener < 0) throw std::runtime_error("cannot create Unix socket");
    listener_.store(listener, std::memory_order_release);
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    std::memcpy(address.sun_path, socket_path_.c_str(), socket_path_.size() + 1U);
    if (::bind(listener, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) != 0
        || ::listen(listener, 16) != 0) {
        throw std::runtime_error("cannot bind/listen on " + socket_path_ + ": " + std::strerror(errno));
    }
    ::chmod(socket_path_.c_str(), 0660);
    std::jthread watchdog([this](std::stop_token token) {
        while (!token.stop_requested() && !stopping_.load(std::memory_order_relaxed)) {
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
            clear_expired_lease(backend_);
        }
    });
    while (!stopping_.load(std::memory_order_relaxed)) {
        const auto client = ::accept(listener, nullptr, nullptr);
        if (client < 0) {
            if (errno == EINTR) continue;
            if (stopping_.load(std::memory_order_relaxed)) break;
            throw std::runtime_error("accept failed: " + std::string(std::strerror(errno)));
        }
        ClientSlot* slot{};
        {
            std::scoped_lock lock(clients_mutex_);
            for (auto& candidate : clients_)
                if (candidate.descriptor < 0) { slot = &candidate; break; }
        }
        if (slot == nullptr) { ::close(client); continue; }
        if (slot->worker.joinable()) slot->worker.join();
        {
            std::scoped_lock lock(clients_mutex_);
            slot->descriptor = client;
        }
        slot->worker = std::jthread([this, slot, client] {
            handle_client(client);
            std::scoped_lock lock(clients_mutex_);
            ::close(client);
            slot->descriptor = -1;
        });
    }
    watchdog.request_stop();
    for (auto& slot : clients_) if (slot.worker.joinable()) slot.worker.join();
}

void NativeProtocolServer::request_stop() noexcept {
    stopping_.store(true, std::memory_order_relaxed);
    {
        std::scoped_lock lock(clients_mutex_);
        for (const auto& slot : clients_)
            if (slot.descriptor >= 0) ::shutdown(slot.descriptor, SHUT_RDWR);
    }
    const auto listener = listener_.exchange(-1, std::memory_order_acq_rel);
    if (listener >= 0) {
        ::shutdown(listener, SHUT_RDWR);
        ::close(listener);
    }
}

void NativeProtocolServer::handle_client(int client) {
    try {
        while (!stopping_.load(std::memory_order_relaxed)) {
            auto request = read_frame(client);
            if (!request.has_value()) break;
            json reply;
            try {
                reply = dispatch(*request, client, backend_, controller_version_, paths_);
            } catch (const ProtocolError& error) {
                reply = error_response(*request, error);
            } catch (const std::invalid_argument& error) {
                reply = error_response(*request, ProtocolError("invalid_argument", error.what()));
            } catch (const std::logic_error& error) {
                reply = error_response(*request, ProtocolError("invalid_state", error.what()));
            } catch (const nlohmann::json::exception& error) {
                reply = error_response(*request, ProtocolError("invalid_request", error.what()));
            } catch (const std::runtime_error& error) {
                reply = error_response(*request, ProtocolError("operation_failed", error.what()));
            }
            const auto kind = request->find("kind");
            if (kind != request->end() && kind->is_string()
                && is_leased_command(kind->get<std::string>())) {
                complete_lease_command(*request, client, reply);
            }
            write_frame(client, reply);
        }
    } catch (const std::exception&) {
        // Connection failures revoke only this client's lease and leave the service alive.
    }
    release_client_lease(client, backend_);
}

} // namespace ecmotion

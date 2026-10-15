// This file is compiled alongside the compiler's generated.hpp. Symbol names and
// IEC wrappers are authoritative; no debugger index or byte-offset assumptions.
#include "generated.hpp"
#include "motion_bridge.hpp"
#include <cstring>

extern "C" void workbench_motion_after_scan(bool plc_running) {
    using namespace strucpp;
    using namespace workbench_motion;
    if (plc_running) MB_SCANCOUNTER = static_cast<std::uint64_t>(MB_SCANCOUNTER) + 1;
    Command command{};
    command.request = MB_REQUESTID;
    command.path = MB_PATHID;
    command.version = MB_VERSION;
    const auto& hash = MB_HASH.get();
    const auto length = hash.length();
    if (length == 64) std::memcpy(command.hash.data(), hash.c_str(), 64);
    command.scan = MB_SCANCOUNTER;
    command.flags = (plc_running ? 1U : 0U) | (MB_STOP ? 2U : 0U);
    Feedback result{};
    for (unsigned i = 0; i < 16 && poll(result); ++i) {
        if (result.request == command.request) {
            MB_FEEDBACKID = result.request;
            MB_BUSY = result.busy != 0;
            MB_DONE = result.done != 0;
            MB_ERRORID = static_cast<std::uint32_t>(result.error);
            MB_PROGRESS = result.progress;
        }
    }
    if (!submit(command)) {
        MB_FEEDBACKID = command.request;
        MB_BUSY = false;
        MB_DONE = false;
        MB_ERRORID = static_cast<std::uint32_t>(transport_error);
    }
}

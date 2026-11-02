#include "control_backend.hpp"
#include <chrono>
namespace ecmotion {
std::uint64_t ControlBackend::scan_batch(const std::vector<RealtimeCommand>& commands,
    const std::vector<std::string>& axes, const std::vector<std::string>& outputs,
    std::uint64_t deadline_ns) {
    const auto now = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
    if (deadline_ns < static_cast<std::uint64_t>(now) || commands.empty() || commands.size() > 16)
        throw std::invalid_argument("Expired or invalid scan batch.");
    // Backends without a realtime transaction implementation reject mixed batches.
    RealtimeCommand c=commands[0];
    for (std::size_t i=1;i<commands.size();++i) {
        if(c.kind!=RealtimeCommandKind::WriteOutputs || commands[i].kind!=c.kind
            || (c.output_mask & commands[i].output_mask)!=0)
            throw std::invalid_argument("Backend requires a single motion action or disjoint DO batch.");
        c.output_mask |= commands[i].output_mask; c.output_values |= commands[i].output_values;
    }
    if(c.kind==RealtimeCommandKind::WriteOutputs) {
        if(c.output_mask == 0 || (c.output_values & ~c.output_mask) != 0)
            throw std::invalid_argument("Invalid digital outputs.");
        std::map<std::string,bool> values;
        for(std::size_t i=0;i<outputs.size() && i<64;++i)
            if(c.output_mask & (std::uint64_t{1}<<i)) values[outputs[i]]=(c.output_values & (std::uint64_t{1}<<i))!=0;
        if(outputs.size()<64 && (c.output_mask>>outputs.size())!=0) throw std::invalid_argument("Unknown output.");
        write_outputs(values); return 0;
    }
    if(c.axis_index>=axes.size()) throw std::invalid_argument("Unknown axis.");
    const auto& a=axes[c.axis_index];
    if (c.motion_profile_version == 2) return axis_profile_command(a,c);
    switch(c.kind) {
        case RealtimeCommandKind::Enable: enable(a); return 0;
        case RealtimeCommandKind::Home: home(a); return 0;
        case RealtimeCommandKind::Stop: stop(a); return 0;
        case RealtimeCommandKind::MoveAbsolute: return move_absolute(a,c.position,c.velocity);
        case RealtimeCommandKind::GroupLine:
        case RealtimeCommandKind::GroupArc:
        case RealtimeCommandKind::GroupSpatialLine:
        case RealtimeCommandKind::GroupSpatialArc: {
            auto name=[&](unsigned slot, std::uint16_t index) {
                if(slot>=c.group_axis_count) return std::string{};
                if(index>=axes.size()) throw std::invalid_argument("Unknown group axis.");
                return axes[index];
            };
            return move_group({c.kind==RealtimeCommandKind::GroupLine?GroupMotionKind::Line
                :c.kind==RealtimeCommandKind::GroupArc?GroupMotionKind::Arc
                :c.kind==RealtimeCommandKind::GroupSpatialLine?GroupMotionKind::SpatialLine:GroupMotionKind::SpatialArc,
                a,name(1,c.secondary_axis_index),c.position,c.secondary_position,c.velocity,c.acceleration,c.deceleration,
                c.center_x,c.center_y,c.clockwise,name(2,c.third_axis_index),name(3,c.fourth_axis_index),
                c.third_position,c.fourth_position,c.center_z,c.normal_x,c.normal_y,c.normal_z});
        }
        default: throw std::invalid_argument("Unsupported scan action.");
    }
}
}

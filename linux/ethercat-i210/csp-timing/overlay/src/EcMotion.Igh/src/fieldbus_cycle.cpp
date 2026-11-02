#include "fieldbus_cycle.hpp"
#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace ecmotion {

bool FieldbusCycle::validate_path(const RealtimePath& path) const noexcept {
    if (path_ || group_.active() || path.count<2 || path.count>RealtimePath::maximum_points
        || path.axis_count<2 || path.axis_count>3) return false;
    for (std::size_t i=0;i<path.axis_count;++i) {
        if (path.axes[i]>=axes_.size()) return false;
        for (std::size_t j=0;j<i;++j) if (path.axes[i]==path.axes[j]) return false;
        const auto* axis=axes_[path.axes[i]];
        const auto start=axis->group_start_position();
        if (!axis->can_start_group(path.points[path.count-1][i],std::min(start,path.minimum[i]),
            std::max(start,path.maximum[i]),path.velocity,path.acceleration,path.deceleration,0)) return false;
    }
    return true;
}
bool FieldbusCycle::start_path_segment() noexcept {
    if (!path_ || path_point_>=path_->count) return false;
    std::array<double,4> start{};
    for (std::size_t i=0;i<path_->axis_count;++i) start[i]=axes_[path_->axes[i]]->group_start_position();
    // A recorded first point can already be the current position; omit only this zero move.
    if (!path_point_ && start==path_->points[0]) ++path_point_;
    const auto& target=path_->points[path_point_];
    const RealtimeCommand command{.sequence=path_id_,
        .kind=path_->axis_count==2?RealtimeCommandKind::GroupLine:RealtimeCommandKind::GroupSpatialLine,
        .axis_index=path_->axes[0], .secondary_axis_index=path_->axes[1], .third_axis_index=path_->axes[2],
        .group_axis_count=path_->axis_count, .position=target[0], .secondary_position=target[1], .third_position=target[2],
        .velocity=path_->velocity, .acceleration=path_->acceleration, .deceleration=path_->deceleration};
    auto preview=group_; GroupGeometry geometry;
    if (!preview.configure(command,start,cycle_seconds_,geometry)) return false;
    for (std::size_t i=0;i<path_->axis_count;++i)
        if (!axes_[path_->axes[i]]->can_start_group(target[i],geometry.minimum[i],geometry.maximum[i],
            path_->velocity*geometry.maximum_axis_slope[i],path_->acceleration*geometry.maximum_axis_slope[i],
            path_->deceleration*geometry.maximum_axis_slope[i],0)) return false;
    group_=preview;
    for (std::size_t i=0;i<path_->axis_count;++i) axes_[path_->axes[i]]->begin_group(path_id_,target[i]);
    return true;
}
void FieldbusCycle::abort_path() noexcept {
    if (!path_) return;
    path_aborted_=true;
    for (std::size_t i=0;i<path_->axis_count;++i) axes_[path_->axes[i]]->apply_safe_state();
    path_=nullptr; exchange_.finish_path(); group_.cancel();
}

FieldbusCycle::FieldbusCycle(RealtimeExchange& exchange, PdoIoCycle& io,
    std::vector<RealtimeAxisAdapter*> axes, double cycle_seconds)
    : exchange_(exchange), io_(io), axes_(std::move(axes)), cycle_seconds_(cycle_seconds) {
    if (axes_.empty() || axes_.size() > RealtimeSnapshot::maximum_axes) {
        throw std::invalid_argument("Fieldbus cycle requires 1..256 axes.");
    }
    for (const auto* axis : axes_) if (!axis) throw std::invalid_argument("Null axis adapter.");
    if (!std::isfinite(cycle_seconds_) || cycle_seconds_ <= 0.0)
        throw std::invalid_argument("Group cycle time must be positive and finite.");
}

RealtimeSnapshot FieldbusCycle::process(
    PdoImageView& image, std::uint64_t sample_monotonic_ns) noexcept {
    const auto safe_state_requested = exchange_.consume_safe_state_request();
    if (safe_state_requested || !bus_ready_) {
        abort_path();
        group_.cancel();
        io_.apply_safe_state();
        for (auto* axis : axes_) axis->apply_safe_state();
        if (safe_state_requested) exchange_.acknowledge_safe_state();
    }
    RealtimeBatch batch;
    while (exchange_.try_dequeue_batch(batch)) {
        const auto sequence = batch.sequence ? batch.sequence : batch.commands[0].sequence;
        bool batch_valid = (batch.count != 0 || batch.io_output_mask || batch.scalar_write_count)
            && batch.count <= batch.commands.size() && batch.scalar_write_count <= batch.scalar_writes.size()
            && (batch.deadline_ns == 0 || sample_monotonic_ns <= batch.deadline_ns)
            && bus_ready_ && !safe_state_requested && !exchange_.is_cancelled(sequence);
        std::array<bool, RealtimeSnapshot::maximum_axes> used_axes{};
        std::array<bool, RealtimeSnapshot::maximum_scalar_channels> used_scalars{};
        std::uint64_t used_outputs{batch.io_output_mask};
        bool used_group{};
        if (batch.io_output_mask)
            batch_valid &= io_.can_apply_command({.kind = RealtimeCommandKind::WriteOutputs,
                .output_mask = batch.io_output_mask, .output_values = batch.io_output_values});
        else batch_valid &= batch.io_output_values == 0;
        for (std::size_t i=0;i<batch.scalar_write_count && batch_valid;++i) {
            const auto& write = batch.scalar_writes[i];
            batch_valid = write.index < used_scalars.size() && !used_scalars[write.index]
                && io_.can_apply_command({.kind = RealtimeCommandKind::WriteScalarOutput,
                    .scalar_index = write.index, .scalar_raw_bits = write.raw_bits});
            if (batch_valid) used_scalars[write.index] = true;
        }
        for(std::size_t i=0;i<batch.count && batch_valid;++i) {
            const auto& c=batch.commands[i];
            if(exchange_.is_cancelled(c.sequence)) { batch_valid=false; break; }
            if (c.kind==RealtimeCommandKind::StartPath) {
                const auto* ready=exchange_.ready_path(c.sequence);
                batch_valid=batch.count==1 && !batch.io_output_mask && !batch.scalar_write_count
                    && ready && validate_path(*ready);
                continue;
            }
            if (c.kind == RealtimeCommandKind::WriteScalarOutput) {
                batch_valid = c.scalar_index < used_scalars.size()
                    && !used_scalars[c.scalar_index] && io_.can_apply_command(c);
                if (batch_valid) used_scalars[c.scalar_index] = true;
                continue;
            }
            if(c.kind==RealtimeCommandKind::WriteOutputs) {
                batch_valid = c.output_mask != 0 && (c.output_values & ~c.output_mask)==0
                    && (used_outputs & c.output_mask)==0
                    && io_.can_apply_command(c);
                used_outputs |= c.output_mask;
                continue;
            }
            const bool grouped=c.kind==RealtimeCommandKind::GroupLine || c.kind==RealtimeCommandKind::GroupArc
                || c.kind==RealtimeCommandKind::GroupSpatialLine || c.kind==RealtimeCommandKind::GroupSpatialArc;
            const auto count=grouped?c.group_axis_count:1;
            const std::array<std::uint16_t,4> indices{c.axis_index,c.secondary_axis_index,c.third_axis_index,c.fourth_axis_index};
            if(count<1 || count>4) { batch_valid=false; break; }
            for(int j=0;j<count && batch_valid;++j) {
                batch_valid=indices[j]<axes_.size() && !used_axes[indices[j]];
                if(batch_valid) used_axes[indices[j]]=true;
            }
            if(c.motion_profile_version && (c.motion_profile_version!=2 || grouped)) batch_valid=false;
            if(grouped && batch_valid) {
                batch_valid=!used_group && !group_.active(); used_group=true;
                auto preview=group_; GroupGeometry geometry; std::array<double,4> start{};
                for(int j=0;j<count;++j) start[j]=axes_[indices[j]]->group_start_position();
                batch_valid &= preview.configure(c,start,cycle_seconds_,geometry);
                const std::array<double,4> targets{c.position,c.secondary_position,c.third_position,c.fourth_position};
                for(int j=0;j<count && batch_valid;++j) {
                    auto slope=geometry.maximum_axis_slope[j];
                    batch_valid=axes_[indices[j]]->can_start_group(targets[j],geometry.minimum[j],geometry.maximum[j],
                        c.velocity*slope,c.acceleration*slope,c.deceleration*slope,j==3?0:geometry.radius);
                }
            } else if(batch_valid && (batch.count>1 || batch.io_output_mask || batch.scalar_write_count || c.motion_profile_version==2)) {
                if(group_.active()) for(std::size_t j=0;j<group_.axis_count();++j)
                    if(c.axis_index==group_.axis_index(j)) batch_valid=false;
                batch_valid &= axes_[c.axis_index]->can_apply_command(c);
            }
        }
        if(!batch_valid) {
            for (std::size_t i=0;i<batch.count;++i)
                if (batch.commands[i].kind==RealtimeCommandKind::StartPath && exchange_.ready_path(batch.commands[i].sequence))
                    exchange_.finish_path();
            ++rejected_commands_;
            exchange_.complete(sequence,false);
            continue;
        }
        bool all_accepted=true;
        if (batch.io_output_mask)
            all_accepted &= io_.apply_command({.kind = RealtimeCommandKind::WriteOutputs,
                .output_mask = batch.io_output_mask, .output_values = batch.io_output_values});
        for (std::size_t i=0;i<batch.scalar_write_count;++i)
            all_accepted &= io_.apply_command({.kind = RealtimeCommandKind::WriteScalarOutput,
                .scalar_index = batch.scalar_writes[i].index, .scalar_raw_bits = batch.scalar_writes[i].raw_bits});
        if (all_accepted && (batch.io_output_mask || batch.scalar_write_count)) {
            last_applied_command_sequence_ = sequence;
            last_applied_command_cycle_ = cycles_ + 1;
        }
        for(std::size_t item=0;item<batch.count;++item) {
        const auto& command=batch.commands[item];
        bool accepted = false;
        if (bus_ready_ && !safe_state_requested && !exchange_.is_cancelled(command.sequence)) {
            if (command.kind==RealtimeCommandKind::StartPath) {
                path_=exchange_.ready_path(command.sequence); path_id_=command.sequence; path_point_=0;
                if (path_) {
                    path_aborted_=false; path_axes_=path_->axes; path_axis_count_=path_->axis_count;
                    path_total_points_=path_->count;
                    exchange_.activate_path(); accepted=start_path_segment();
                }
                if (!accepted) abort_path();
            } else if (command.kind == RealtimeCommandKind::GroupLine
                || command.kind == RealtimeCommandKind::GroupArc
                || command.kind == RealtimeCommandKind::GroupSpatialLine
                || command.kind == RealtimeCommandKind::GroupSpatialArc) {
                const auto spatial = command.kind == RealtimeCommandKind::GroupSpatialLine
                    || command.kind == RealtimeCommandKind::GroupSpatialArc;
                const auto count = spatial ? command.group_axis_count : std::uint8_t{2};
                const std::array<std::uint16_t, 4> indices{command.axis_index,
                    command.secondary_axis_index, command.third_axis_index,
                    command.fourth_axis_index};
                bool indices_valid = count >= 2 && count <= 4;
                for (std::size_t slot = 0; slot < count && indices_valid; ++slot)
                    indices_valid = indices[slot] < axes_.size();
                if (!group_.active() && indices_valid) {
                    std::array<double, 4> start{};
                    for (std::size_t slot = 0; slot < count; ++slot)
                        start[slot] = axes_[indices[slot]]->group_start_position();
                    GroupGeometry geometry;
                    accepted = group_.configure(command, start, cycle_seconds_, geometry);
                    const std::array<double, 4> targets{command.position,
                        command.secondary_position, command.third_position,
                        command.fourth_position};
                    for (std::size_t slot = 0; slot < count && accepted; ++slot) {
                        const auto slope = geometry.maximum_axis_slope[slot];
                        accepted = axes_[indices[slot]]->can_start_group(targets[slot],
                            geometry.minimum[slot], geometry.maximum[slot],
                            command.velocity*slope, command.acceleration*slope,
                            command.deceleration*slope,
                            slot == 3 ? 0.0 : geometry.radius);
                    }
                    if (accepted) {
                        for (std::size_t slot = 0; slot < count; ++slot)
                            axes_[indices[slot]]->begin_group(command.sequence, targets[slot]);
                        for (std::size_t slot = 0; slot < count; ++slot)
                            if (axes_[indices[slot]]->group_faulted(command.sequence)) accepted = false;
                        if (!accepted)
                            for (std::size_t slot = 0; slot < count; ++slot)
                                axes_[indices[slot]]->apply_safe_state();
                    }
                    if (!accepted) group_.cancel();
                }
            } else if (command.kind == RealtimeCommandKind::WriteOutputs
                || command.kind == RealtimeCommandKind::WriteScalarOutput) {
                accepted = io_.apply_command(command);
            } else if (command.axis_index < axes_.size()) {
                bool touches_group = false;
                if (group_.active())
                    for (std::size_t slot = 0; slot < group_.axis_count(); ++slot)
                        touches_group |= command.axis_index == group_.axis_index(slot);
                if (touches_group && (command.kind == RealtimeCommandKind::Stop
                    || command.kind == RealtimeCommandKind::Disable
                    || command.kind == RealtimeCommandKind::Reset)) {
                    for (std::size_t slot = 0; slot < group_.axis_count(); ++slot)
                        axes_[group_.axis_index(slot)]->apply_safe_state();
                    group_.cancel();
                    abort_path();
                    accepted = true;
                } else if (!touches_group) {
                    accepted = axes_[command.axis_index]->apply_command(command);
                }
            }
        }
        if (!accepted) ++rejected_commands_;
        else {
            last_applied_command_sequence_ = command.sequence;
            last_applied_command_cycle_ = cycles_ + 1;
        }
        all_accepted &= accepted;
        }
        if(!all_accepted && (batch.count>1 || batch.io_output_mask || batch.scalar_write_count)) {
            abort_path();
            io_.apply_safe_state(); group_.cancel();
            for(auto* axis:axes_) axis->apply_safe_state();
        }
        exchange_.complete(sequence, all_accepted);
    }
    if (group_.active()) {
        bool faulted = false;
        for (std::size_t slot = 0; slot < group_.axis_count(); ++slot)
            faulted |= axes_[group_.axis_index(slot)]->group_faulted(group_.motion_id());
        if (faulted) {
            abort_path();
            for (std::size_t slot = 0; slot < group_.axis_count(); ++slot)
                axes_[group_.axis_index(slot)]->apply_safe_state();
            group_.cancel();
        } else {
            const auto point = group_.advance();
            for (std::size_t slot = 0; slot < group_.axis_count(); ++slot)
                axes_[group_.axis_index(slot)]->group_setpoint(
                    point.position[slot], point.velocity[slot], point.final_point);
        }
    }
    RealtimeSnapshot snapshot;
    io_.sample(snapshot);
    snapshot.axis_count = static_cast<std::uint16_t>(axes_.size());
    for (std::size_t i = 0; i < axes_.size(); ++i) {
        auto& axis_value = snapshot.axes[i];
        axes_[i]->process(image, axis_value);
        if (i == 0) {
            snapshot.state = axis_value.state;
            snapshot.actual_position = axis_value.actual_position;
            snapshot.actual_velocity = axis_value.actual_velocity;
            snapshot.homed = axis_value.homed;
            snapshot.statusword = axis_value.statusword;
            snapshot.mode_display = axis_value.mode_display;
            snapshot.error_code = axis_value.error_code;
            snapshot.commissioning_required = axis_value.commissioning_required;
            snapshot.software_limit_violation = axis_value.software_limit_violation;
            snapshot.following_error_fault = axis_value.following_error_fault;
            snapshot.overspeed_fault = axis_value.overspeed_fault;
        }
    }
    if (group_.active()) {
        bool faulted = false;
        bool all_done = group_.trajectory_finished();
        for (std::size_t slot = 0; slot < group_.axis_count(); ++slot) {
            const auto* axis = axes_[group_.axis_index(slot)];
            faulted |= axis->group_faulted(group_.motion_id());
            all_done &= axis->group_done(group_.motion_id());
        }
        if (faulted) {
            abort_path();
            for (std::size_t slot = 0; slot < group_.axis_count(); ++slot)
                axes_[group_.axis_index(slot)]->apply_safe_state();
            group_.cancel();
        } else if (all_done) {
            group_.cancel();
            if (path_) {
                if (++path_point_>=path_->count) { path_=nullptr; exchange_.finish_path(); }
                else if (!start_path_segment()) abort_path();
            }
        }
    }
    // Segment feedback must never expose intermediate Done to PLC/HMI consumers.
    if (path_) for (std::size_t i=0;i<path_->axis_count;++i) {
        auto& axis=snapshot.axes[path_->axes[i]];
        axis.motion_done=false; axis.motion_busy=true;
        axis.motion_target=path_->points[path_->count-1][i];
    }
    if (path_aborted_) for (std::size_t i=0;i<path_axis_count_;++i) {
        auto& axis=snapshot.axes[path_axes_[i]];
        if (axis.motion_id==path_id_) { axis.motion_done=false; axis.motion_busy=false; axis.motion_aborted=true; }
    }
    snapshot.bus_ready = bus_ready_;
    snapshot.path_execution_id=path_id_;
    snapshot.path_completed_points=path_point_;
    snapshot.path_total_points=path_total_points_;
    snapshot.path_status=path_?1:path_aborted_?3:path_id_?2:0;
    snapshot.cycles = ++cycles_;
    snapshot.sample_monotonic_ns = sample_monotonic_ns;
    snapshot.last_applied_command_sequence = last_applied_command_sequence_;
    snapshot.last_applied_command_cycle = last_applied_command_cycle_;
    snapshot.rejected_commands = rejected_commands_;
    exchange_.publish(snapshot);
    return snapshot;
}

} // namespace ecmotion

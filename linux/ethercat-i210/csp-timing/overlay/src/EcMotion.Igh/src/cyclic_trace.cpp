// SPDX-License-Identifier: MIT
#include "cyclic_trace.hpp"
#include <cerrno>
#include <fcntl.h>
#include <iomanip>
#include <limits>
#include <locale>
#include <sstream>
#include <stdexcept>
#include <system_error>
#include <unistd.h>

namespace ecmotion {
CyclicAxisTrace::CyclicAxisTrace(const PdoLayout& layout,const std::string& prefix,std::size_t capacity) {
    if(capacity==0 || capacity>maximum_capacity) throw std::invalid_argument("Trace capacity must be 1..120000");
    struct Required {const char* suffix;PdoDirection direction;std::uint8_t bits;};
    using D=PdoDirection;
    const std::array<Required,Count> required{{
        {"Target_position.0x607A:0",D::ControllerOutput,32},
        {"Velocity_offset.0x60B1:0",D::ControllerOutput,32},
        {"Position_actual_value.0x6064:0",D::ControllerInput,32},
        {"Velocity_actual_value.0x606C:0",D::ControllerInput,32},
        {"Position_demand.0x6062:0",D::ControllerInput,32},
        {"Following_error.0x60F4:0",D::ControllerInput,32},
        {"Torque_actual.0x6077:0",D::ControllerInput,16},
        {"Controlword.0x6040:0",D::ControllerOutput,16},
        {"Statusword.0x6041:0",D::ControllerInput,16},
        {"Error_code.0x603F:0",D::ControllerInput,16},
        {"Modes_of_operation_display.0x6061:0",D::ControllerInput,8}}};
    for(std::size_t i=0;i<Count;++i) {
        handles_[i]=layout.resolve(prefix+"."+required[i].suffix);
        const auto& entry=layout.entry(handles_[i]);
        if(entry.direction!=required[i].direction || entry.bit_length!=required[i].bits)
            throw std::invalid_argument("Trace PDO type/direction mismatch: "+entry.key);
    }
    storage_.resize(capacity); // allocate and touch all pages before entering RT
}
std::int32_t CyclicAxisTrace::target_raw(const PdoImageView& image) const noexcept {
    return static_cast<std::int32_t>(image.read_signed(handles_[Target]));
}
void CyclicAxisTrace::capture(const PdoImageView& image,const RealtimeAxisSnapshot& axis,
    std::uint64_t cycle,std::uint64_t scheduled_ns,std::uint64_t monotonic_ns,bool bus_ready,
    std::int32_t previous_target,const CyclicTiming& timing) noexcept {
    const auto shadow=demand_guard_.update(cycle,scheduled_ns,target_raw(image),
        static_cast<std::int32_t>(image.read_signed(handles_[Demand])),
        static_cast<std::int32_t>(image.read_signed(handles_[Actual])),
        static_cast<std::int32_t>(image.read_signed(handles_[DriveError])),
        axis.homed && image.read_signed(handles_[Mode])==8 && image.read_unsigned(handles_[Controlword])==15 && (image.read_unsigned(handles_[Statusword])&4),
        bus_ready && timing.cycle_timing_enabled && timing.delivery.valid && !timing.actual_send_budget_overrun);
    const auto state=state_.load(std::memory_order_acquire);
    if(state==State::FreezeRequested) {state_.store(State::Frozen,std::memory_order_release);return;}
    if(state==State::Frozen) return;
    if(state==State::RearmRequested) {
        size_=0;dropped_=0;state_.store(State::Recording,std::memory_order_release);
    }
    if(size_==storage_.size()) {
        if(dropped_!=std::numeric_limits<std::uint64_t>::max()) ++dropped_;
        return;
    }
    auto& row=storage_[size_++];
    row.timing=timing;row.demand_shadow=shadow;row.homed=axis.homed;
    row.cycle=cycle;row.scheduled_ns=scheduled_ns;row.monotonic_ns=monotonic_ns;
    row.motion_id=axis.motion_id;row.bus_ready=bus_ready;row.state=static_cast<std::uint8_t>(axis.state);
    row.previous_target_raw=previous_target;row.sent_target_raw=target_raw(image);
    row.offset_raw=static_cast<std::int32_t>(image.read_signed(handles_[Offset]));
    row.actual_raw=static_cast<std::int32_t>(image.read_signed(handles_[Actual]));
    row.velocity_raw=static_cast<std::int32_t>(image.read_signed(handles_[Velocity]));
    row.demand_raw=static_cast<std::int32_t>(image.read_signed(handles_[Demand]));
    row.drive_error_raw=static_cast<std::int32_t>(image.read_signed(handles_[DriveError]));
    row.torque_raw=static_cast<std::int16_t>(image.read_signed(handles_[Torque]));
    row.controlword=static_cast<std::uint16_t>(image.read_unsigned(handles_[Controlword]));
    row.statusword=static_cast<std::uint16_t>(image.read_unsigned(handles_[Statusword]));
    row.error_code=static_cast<std::uint16_t>(image.read_unsigned(handles_[ErrorCode]));
    row.mode=static_cast<std::int8_t>(image.read_signed(handles_[Mode]));
    row.cycle_target=axis.cycle_target;row.cycle_error=axis.cycle_error;
    row.actual_position=axis.actual_position;row.actual_velocity=axis.actual_velocity;
}
bool CyclicAxisTrace::request_freeze() noexcept {
    auto expected=State::Recording;
    return state_.compare_exchange_strong(expected,State::FreezeRequested,std::memory_order_acq_rel)
        || expected==State::Frozen || expected==State::FreezeRequested;
}
bool CyclicAxisTrace::frozen() const noexcept {return state_.load(std::memory_order_acquire)==State::Frozen;}
bool CyclicAxisTrace::request_rearm() noexcept {
    auto expected=State::Frozen;
    return state_.compare_exchange_strong(expected,State::RearmRequested,std::memory_order_acq_rel);
}
void CyclicAxisTrace::write_csv(std::ostream& output) const {
    output.imbue(std::locale::classic());output<<std::noboolalpha<<std::setprecision(17);
    output<<"# format=ecmotion-cyclic-axis-trace-v2\n# capacity="<<storage_.size()<<"\n# dropped="<<dropped_<<'\n';
    output<<"cycle,scheduled_ns,monotonic_ns,bus_ready,motion_id,state,previous_target_raw,sent_target_raw,offset_raw,actual_raw,velocity_raw,demand_raw,drive_error_raw,torque_raw,controlword,statusword,error_code,mode,cycle_target,cycle_error,actual_position,actual_velocity,rx_call_start_ns,rx_call_end_ns,process_end_ns,tx_call_start_ns,tx_call_end_ns,reference_cycle,reference_tx_call_start_ns,reference_time_low32,reference_valid,reference_scheduled_ns,cycle_timing_enabled,motion_timing_ready,delivery_metadata_valid,delivery_reference_phase_valid,delivery_predicted_phase_valid,delivery_reference_step_valid,delivery_valid,delivery_ready,delivery_good_cycles,delivery_reference_phase_ns,delivery_predicted_phase_ns,delivery_reference_step_ns,pre_send_check_ns,pre_send_budget_overrun,actual_send_budget_overrun,send_host_deadline_overrun,homed,shadow_eligible,shadow_ready,shadow_fault,shadow_error_raw,shadow_excess_cycles\n";
    for(const auto& r:frames()) output<<r.cycle<<','<<r.scheduled_ns<<','<<r.monotonic_ns<<','<<static_cast<unsigned>(r.bus_ready)<<','<<r.motion_id<<','<<static_cast<unsigned>(r.state)<<','
        <<r.previous_target_raw<<','<<r.sent_target_raw<<','<<r.offset_raw<<','<<r.actual_raw<<','<<r.velocity_raw<<','<<r.demand_raw<<','<<r.drive_error_raw<<','<<r.torque_raw<<','
        <<r.controlword<<','<<r.statusword<<','<<r.error_code<<','<<static_cast<int>(r.mode)<<','<<r.cycle_target<<','<<r.cycle_error<<','<<r.actual_position<<','<<r.actual_velocity<<','<<r.timing.rx_start_ns<<','<<r.timing.rx_end_ns<<','<<r.timing.process_end_ns<<','<<r.timing.tx_start_ns<<','<<r.timing.tx_end_ns<<','<<r.timing.reference_cycle<<','<<r.timing.reference_tx_start_ns<<','<<r.timing.reference_time_low32<<','<<static_cast<unsigned>(r.timing.reference_valid)<<','
        <<r.timing.reference_scheduled_ns<<','<<r.timing.cycle_timing_enabled<<','<<r.timing.motion_timing_ready<<','
        <<r.timing.delivery.metadata_valid<<','<<r.timing.delivery.reference_phase_valid<<','<<r.timing.delivery.predicted_phase_valid<<','<<r.timing.delivery.reference_step_valid<<','
        <<r.timing.delivery.valid<<','<<r.timing.delivery.ready<<','<<r.timing.delivery.good_cycles<<','<<r.timing.delivery.reference_phase_ns<<','<<r.timing.delivery.predicted_phase_ns<<','<<r.timing.delivery.reference_step_ns<<','
        <<r.timing.pre_send_check_ns<<','<<r.timing.pre_send_budget_overrun<<','<<r.timing.actual_send_budget_overrun<<','<<r.timing.send_host_deadline_overrun<<','<<r.homed<<','<<r.demand_shadow.eligible<<','<<r.demand_shadow.ready<<','<<r.demand_shadow.fault<<','<<r.demand_shadow.error<<','<<r.demand_shadow.excess<<'\n';
    output<<"# complete=1\n";
    if(!output) throw std::runtime_error("Trace CSV serialization failed");
}
void CyclicAxisTrace::write_csv_exclusive(const std::string& path) const {
    std::ostringstream output;write_csv(output);const auto bytes=output.str();
    const auto fd=::open(path.c_str(),O_WRONLY|O_CREAT|O_EXCL|O_NOFOLLOW|O_CLOEXEC,0640);
    if(fd<0) throw std::system_error(errno,std::generic_category(),"Create trace CSV");
    std::size_t written=0;
    while(written<bytes.size()) {
        const auto count=::write(fd,bytes.data()+written,bytes.size()-written);
        if(count<0 && errno==EINTR) continue;
        if(count<=0) {const auto error=count<0?errno:EIO;::close(fd);throw std::system_error(error,std::generic_category(),"Write trace CSV");}
        written+=static_cast<std::size_t>(count);
    }
    if(::close(fd)!=0) throw std::system_error(errno,std::generic_category(),"Close trace CSV");
}
}

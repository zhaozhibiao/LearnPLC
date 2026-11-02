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
    std::int32_t previous_target) noexcept {
    if(size_==storage_.size()) {
        if(dropped_!=std::numeric_limits<std::uint64_t>::max()) ++dropped_;
        return;
    }
    auto& row=storage_[size_++];
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
void CyclicAxisTrace::write_csv(std::ostream& output) const {
    output.imbue(std::locale::classic());output<<std::setprecision(17);
    output<<"# format=ecmotion-cyclic-axis-trace-v1\n# capacity="<<storage_.size()<<"\n# dropped="<<dropped_<<'\n';
    output<<"cycle,scheduled_ns,monotonic_ns,bus_ready,motion_id,state,previous_target_raw,sent_target_raw,offset_raw,actual_raw,velocity_raw,demand_raw,drive_error_raw,torque_raw,controlword,statusword,error_code,mode,cycle_target,cycle_error,actual_position,actual_velocity\n";
    for(const auto& r:frames()) output<<r.cycle<<','<<r.scheduled_ns<<','<<r.monotonic_ns<<','<<static_cast<unsigned>(r.bus_ready)<<','<<r.motion_id<<','<<static_cast<unsigned>(r.state)<<','
        <<r.previous_target_raw<<','<<r.sent_target_raw<<','<<r.offset_raw<<','<<r.actual_raw<<','<<r.velocity_raw<<','<<r.demand_raw<<','<<r.drive_error_raw<<','<<r.torque_raw<<','
        <<r.controlword<<','<<r.statusword<<','<<r.error_code<<','<<static_cast<int>(r.mode)<<','<<r.cycle_target<<','<<r.cycle_error<<','<<r.actual_position<<','<<r.actual_velocity<<'\n';
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

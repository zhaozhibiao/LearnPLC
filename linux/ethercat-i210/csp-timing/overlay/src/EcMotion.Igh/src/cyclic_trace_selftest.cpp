// SPDX-License-Identifier: MIT
#include "cyclic_trace.hpp"
#include <array>
#include <climits>
#include <sstream>
#include <stdexcept>
#include <iostream>
#include <filesystem>
#include <fstream>
#include <unistd.h>

namespace {
void check(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
std::vector<ecmotion::PdoEntryDescriptor> descriptors() {
    using D=ecmotion::PdoDirection;
    return {{"Axis1Drive.Controlword.0x6040:0",D::ControllerOutput,0,0,16},
        {"Axis1Drive.Target_position.0x607A:0",D::ControllerOutput,2,0,32},
        {"Axis1Drive.Velocity_offset.0x60B1:0",D::ControllerOutput,6,0,32},
        {"Axis1Drive.Modes_of_operation.0x6060:0",D::ControllerOutput,10,0,8},
        {"Axis1Drive.Error_code.0x603F:0",D::ControllerInput,11,0,16},
        {"Axis1Drive.Statusword.0x6041:0",D::ControllerInput,13,0,16},
        {"Axis1Drive.Position_actual_value.0x6064:0",D::ControllerInput,15,0,32},
        {"Axis1Drive.Velocity_actual_value.0x606C:0",D::ControllerInput,19,0,32},
        {"Axis1Drive.Modes_of_operation_display.0x6061:0",D::ControllerInput,23,0,8},
        {"Axis1Drive.Position_demand.0x6062:0",D::ControllerInput,24,0,32},
        {"Axis1Drive.Following_error.0x60F4:0",D::ControllerInput,28,0,32},
        {"Axis1Drive.Torque_actual.0x6077:0",D::ControllerInput,32,0,16}};
}
template<class F> void rejected(F action) {
    bool failed=false;try { action(); } catch(const std::exception&) { failed=true; }
    check(failed,"invalid trace configuration accepted");
}
}
int main() {
    try {
        auto entries=descriptors();ecmotion::PdoLayout layout(entries,34);
        std::array<std::uint8_t,34> bytes{};ecmotion::PdoImageView image(bytes,layout);
        image.write_from_controller(layout.resolve(entries[1].key),static_cast<std::uint32_t>(-100));
        image.write_from_controller(layout.resolve(entries[2].key),static_cast<std::uint32_t>(-200));
        image.write_from_device(layout.resolve(entries[5].key),0x8250);
        image.write_from_device(layout.resolve(entries[6].key),static_cast<std::uint32_t>(INT32_MIN));
        image.write_from_device(layout.resolve(entries[7].key),static_cast<std::uint32_t>(-321));
        image.write_from_device(layout.resolve(entries[8].key),8);
        image.write_from_device(layout.resolve(entries[9].key),static_cast<std::uint32_t>(INT32_MAX));
        image.write_from_device(layout.resolve(entries[10].key),static_cast<std::uint32_t>(-400));
        image.write_from_device(layout.resolve(entries[11].key),static_cast<std::uint16_t>(-123));
        ecmotion::CyclicAxisTrace trace(layout,"Axis1Drive",2);
        auto old=trace.target_raw(image);
        image.write_from_controller(layout.resolve(entries[1].key),static_cast<std::uint32_t>(-99));
        const auto before_capture=bytes;
        ecmotion::RealtimeAxisSnapshot axis;axis.motion_id=7;axis.cycle_target=-.5;axis.cycle_error=-.125;
        ecmotion::CyclicTiming timing;
        timing.rx_start_ns=1000510;timing.rx_end_ns=1000520;timing.process_end_ns=1000540;
        timing.tx_start_ns=1000550;timing.tx_end_ns=1000570;
        timing.reference_valid=true;timing.reference_time_low32=0xfffffff0;
        timing.reference_cycle=0;timing.reference_tx_start_ns=550;
        trace.capture(image,axis,1,1000000,1000500,true,old,timing);
        trace.capture(image,axis,2,2000000,2000500,false,-99);
        trace.capture(image,axis,3,3000000,3000500,true,-99);
        check(trace.frames().size()==2 && trace.dropped()==1,"trace capacity was not bounded");
        check(trace.request_freeze(),"recording trace freeze rejected");
        check(!trace.frozen(),"freeze must await RT acknowledgment");
        trace.capture(image,axis,4,4000000,4000500,true,-99);
        check(trace.frozen() && trace.frames().size()==2 && trace.dropped()==1,"freeze handshake");
        trace.capture(image,axis,5,5000000,5000500,true,-99);
        check(trace.frames().size()==2 && trace.dropped()==1,"frozen capture mutated buffer");
        const auto& row=trace.frames()[0];
        check(row.previous_target_raw==-100 && row.sent_target_raw==-99,"cycle targets mixed");
        check(row.actual_raw==INT32_MIN && row.demand_raw==INT32_MAX,"int32 boundary decode failed");
        check(row.drive_error_raw==-400 && row.torque_raw==-123 && row.velocity_raw==-321,"signed decode failed");
        check(row.offset_raw==-200 && row.monotonic_ns==1000500 && row.scheduled_ns==1000000,"trace metadata missing");
        check(row.timing.rx_start_ns==1000510 && row.timing.tx_end_ns==1000570 && row.timing.reference_time_low32==0xfffffff0,"cycle timing metadata lost");
        check(row.timing.reference_valid && row.timing.reference_tx_start_ns==550,"reference timing association lost");
        check(row.motion_id==7 && row.cycle_error==-.125 && !trace.frames()[1].bus_ready,"sample validity mixed");
        check(trace.target_raw(image)==-99 && bytes==before_capture,"passive recorder changed PDO");
        std::ostringstream csv;trace.write_csv(csv);
        check(csv.str().find("previous_target_raw,sent_target_raw")!=std::string::npos,"CSV header missing");
        check(csv.str().find("-2147483648")!=std::string::npos && csv.str().find("2147483647")!=std::string::npos,"CSV integer precision lost");
        check(csv.str().find("# dropped=1")!=std::string::npos,"overflow metadata missing");
        for(const auto* column:{"cycle_timing_enabled","motion_timing_ready","delivery_reference_phase_ns","delivery_predicted_phase_ns","delivery_valid"})
            check(csv.str().find(column)!=std::string::npos,"delivery timing CSV column missing");
        const auto file=std::filesystem::temp_directory_path()/("ecmotion-trace-test-"+std::to_string(::getpid())+".csv");
        trace.write_csv_exclusive(file.string());
        rejected([&]{trace.write_csv_exclusive(file.string());});
        std::ifstream saved(file);const std::string contents{std::istreambuf_iterator<char>(saved),{}};
        check(contents==csv.str(),"CSV file export differs or overwrote existing data");
        saved.close();std::filesystem::remove(file);
        rejected([&]{ecmotion::CyclicAxisTrace bad(layout,"Axis1Drive",0);});
        rejected([&]{ecmotion::CyclicAxisTrace bad(layout,"Axis1Drive",120001);});
        auto wrong=entries;wrong[9].bit_length=16;
        rejected([&]{ecmotion::PdoLayout l(wrong,34);ecmotion::CyclicAxisTrace bad(l,"Axis1Drive",1);});
        wrong=entries;wrong[9].direction=ecmotion::PdoDirection::ControllerOutput;
        rejected([&]{ecmotion::PdoLayout l(wrong,34);ecmotion::CyclicAxisTrace bad(l,"Axis1Drive",1);});
        wrong=entries;wrong.pop_back();
        rejected([&]{ecmotion::PdoLayout l(wrong,34);ecmotion::CyclicAxisTrace bad(l,"Axis1Drive",1);});
        check(trace.request_rearm(),"frozen trace rearm rejected");
        check(!trace.request_freeze(),"freeze must reject pending rearm, never wait forever");
        trace.capture(image,axis,9,9000000,9000500,true,-99);
        check(trace.frames().size()==1 && trace.frames()[0].cycle==9 && trace.dropped()==0,"trace rearm did not reset bounded buffer");
        check(!trace.request_rearm(),"recording trace must reject rearm");
        std::cout<<"cyclic axis trace: PASS\n";return 0;
    } catch(const std::exception& e) {std::cerr<<e.what()<<'\n';return 1;}
}

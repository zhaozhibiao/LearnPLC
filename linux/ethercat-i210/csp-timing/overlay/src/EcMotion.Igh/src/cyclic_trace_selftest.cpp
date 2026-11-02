// SPDX-License-Identifier: MIT
#include "cyclic_trace.hpp"
#include "axis_feedback.hpp"
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
        // Formal engineering keys are not legacy display-name suffixes.
        auto formal=descriptors();
        for(auto& entry:formal) entry.key="Project.Axis."+entry.key.substr(entry.key.find("0x"));
        formal.pop_back(); // Engineering mapping intentionally has no 6077 torque.
        ecmotion::AxisFeedbackConfiguration configured;
        configured.controlword_key=formal[0].key; configured.target_position_key=formal[1].key;
        configured.velocity_offset_key=formal[2].key; configured.statusword_key=formal[5].key;
        configured.position_actual_key=formal[6].key; configured.velocity_actual_key=formal[7].key;
        configured.mode_display_key=formal[8].key; configured.error_code_key=formal[4].key;
        configured.position_demand_key=formal[9].key; configured.following_error_key=formal[10].key;
        const auto bindings=ecmotion::CyclicAxisTraceBindings::from_axis(configured);
        ecmotion::PdoLayout formal_layout(formal,34);
        ecmotion::PdoImageView formal_image(bytes,formal_layout);
        ecmotion::CyclicAxisTrace formal_trace(formal_layout,bindings,2);
        formal_trace.capture(formal_image,axis,10,10000000,10000500,true,-99);
        check(formal_trace.request_freeze(),"formal trace freeze rejected");
        formal_trace.capture(formal_image,axis,11,11000000,11000500,true,-99);
        check(formal_trace.frozen() && !formal_trace.frames()[0].torque_valid,"absent torque falsely valid");
        std::ostringstream formal_csv; formal_trace.write_csv(formal_csv);
        check(formal_csv.str().find("torque_raw,torque_valid")!=std::string::npos,"torque validity CSV missing");
        check(formal_csv.str().find("-400,,0,")!=std::string::npos,"unmeasured torque must be blank with validity false");
        formal_trace.capture(formal_image,axis,12,12000000,12000500,true,-99);
        std::ostringstream frozen_csv; formal_trace.write_csv(frozen_csv);
        check(formal_csv.str()==frozen_csv.str(),"formal frozen CSV mutated");
        for(const auto index:{9U,10U}) {
            auto absent=formal; absent.erase(absent.begin()+index);
            rejected([&]{ecmotion::PdoLayout l(absent,34);ecmotion::CyclicAxisTrace bad(l,bindings,1);});
        }
        for(std::size_t index=0;index<formal.size();++index) {
            if(index==3) continue; // mode command is not a trace channel
            auto invalid=formal; invalid[index].direction=invalid[index].direction==ecmotion::PdoDirection::ControllerInput
                ? ecmotion::PdoDirection::ControllerOutput:ecmotion::PdoDirection::ControllerInput;
            rejected([&]{ecmotion::PdoLayout l(invalid,34);ecmotion::CyclicAxisTrace bad(l,bindings,1);});
            invalid=formal; invalid[index].bit_length/=2;
            rejected([&]{ecmotion::PdoLayout l(invalid,34);ecmotion::CyclicAxisTrace bad(l,bindings,1);});
        }
        auto no_demand=bindings; no_demand.position_demand_key.clear();
        rejected([&]{ecmotion::CyclicAxisTrace bad(formal_layout,no_demand,1);});
        auto torque_bindings=bindings; torque_bindings.torque_actual_key="Project.Axis.0x6077:0";
        rejected([&]{ecmotion::CyclicAxisTrace bad(formal_layout,torque_bindings,1);});
        formal.push_back({torque_bindings.torque_actual_key,ecmotion::PdoDirection::ControllerInput,32,0,16});
        ecmotion::PdoLayout torque_layout(formal,34);
        ecmotion::PdoImageView torque_image(bytes,torque_layout);
        ecmotion::CyclicAxisTrace torque_trace(torque_layout,torque_bindings,1);
        torque_trace.capture(torque_image,axis,1,1,1,true,0);
        check(torque_trace.frames()[0].torque_valid && torque_trace.frames()[0].torque_raw==-123,"bound torque lost validity/value");
        check(formal_trace.request_rearm(),"formal rearm rejected");
        formal_trace.capture(formal_image,axis,13,13000000,13000500,true,-99);
        check(formal_trace.frames().size()==1 && formal_trace.frames()[0].cycle==13,"formal rearm failed");
        std::cout<<"cyclic axis trace: PASS\n";return 0;
    } catch(const std::exception& e) {std::cerr<<e.what()<<'\n';return 1;}
}

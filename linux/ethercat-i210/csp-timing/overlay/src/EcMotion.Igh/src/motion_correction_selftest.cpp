#include "axis_feedback.hpp"
#include "cyclic_trace.hpp"
#include <cmath>
#include <iostream>
#include <limits>
#include <nlohmann/json.hpp>
#include <fstream>
#include <filesystem>
#include <unistd.h>
#include <stdexcept>
using namespace ecmotion;
void check(bool ok,const char* msg){if(!ok)throw std::runtime_error(msg);}
int main(int argc,char** argv){try{
 check(argc==2,"machine fixture argument required");
 const auto tmp=std::filesystem::temp_directory_path()/("motion-scale-"+std::to_string(getpid())+".json");
 nlohmann::json root;{std::ifstream in(argv[1]);in>>root;}
 root["axes"][0]["velocityFeedbackRawPerUnitPerSecond"]=2000;
 root["axes"][0]["velocityOffsetRawPerUnitPerSecond"]=4000;
 {std::ofstream out(tmp);out<<root;}
 auto parsed=AxisFeedbackConfiguration::load_all(tmp.string());
 check(parsed[0].velocity_feedback_raw_per_unit_per_second==2000 && parsed[0].velocity_offset_raw_per_unit_per_second==4000,"explicit JSON scales lost");
 root["axes"][0]["velocityFeedbackRawPerUnitPerSecond"]=0;
 {std::ofstream out(tmp);out<<root;}
 bool invalid=false;try{static_cast<void>(AxisFeedbackConfiguration::load_all(tmp.string()));}catch(const std::invalid_argument&){invalid=true;}
 std::filesystem::remove(tmp);check(invalid,"explicit zero JSON scale accepted");
 PdoLayout layout({{"cw",PdoDirection::ControllerOutput,0,0,16},{"tp",PdoDirection::ControllerOutput,2,0,32},{"vo",PdoDirection::ControllerOutput,6,0,32},{"mo",PdoDirection::ControllerOutput,10,0,8},{"sw",PdoDirection::ControllerInput,11,0,16},{"pa",PdoDirection::ControllerInput,13,0,32},{"va",PdoDirection::ControllerInput,17,0,32},{"md",PdoDirection::ControllerInput,21,0,8},{"ec",PdoDirection::ControllerInput,22,0,16}},24);
 std::vector<std::uint8_t> bytes(24);PdoImageView image(bytes,layout);
 AxisFeedbackConfiguration c;c.name="Axis1";c.controlword_key="cw";c.target_position_key="tp";c.velocity_offset_key="vo";c.mode_of_operation_key="mo";c.statusword_key="sw";c.position_actual_key="pa";c.velocity_actual_key="va";c.mode_display_key="md";c.error_code_key="ec";c.counts_per_engineering_unit=1000000;c.direction=1;
 c.motion={.001,-1,1,.2,1000,1000};c.safety={.001,-1,1,1,.005,.02};c.homing.required=false;
 c.velocity_feedback_raw_per_unit_per_second=2000;c.velocity_offset_raw_per_unit_per_second=4000;
 Cia402AxisAdapter axis(layout,c);RealtimeAxisSnapshot s;
 image.write_from_device(layout.resolve("pa"),10000);image.write_from_device(layout.resolve("va"),20);image.write_from_device(layout.resolve("sw"),0x40);image.write_from_device(layout.resolve("md"),8);axis.process(image,s);
 check(std::abs(s.actual_velocity-.01)<1e-12,"velocity feedback independent scale");
 image.write_from_device(layout.resolve("va"),0);image.write_from_device(layout.resolve("sw"),0x27);
 RealtimeCommand enable;enable.kind=RealtimeCommandKind::Enable;check(axis.apply_command(enable),"enable");axis.process(image,s);
 RealtimeCommand move;move.kind=RealtimeCommandKind::MoveAbsolute;move.position=.1;move.velocity=.1;move.sequence=1;check(axis.apply_command(move),"move");
 axis.process(image,s);check(std::abs(s.cycle_target-.01)<1e-12 && std::abs(s.cycle_error)<1e-12,"feedback compared against pending future target");
 check(image.read_signed(layout.resolve("vo"))==400,"velocity offset independent scale");
 const auto sent=image.read_signed(layout.resolve("tp"));image.write_from_device(layout.resolve("pa"),sent);axis.process(image,s);
 check(std::abs(s.cycle_error)<1e-12,"previous sent target attribution");
 axis.set_motion_timing_ready(false);axis.process(image,s);
 check(!s.motion_timing_ready && s.motion_timing_fault && s.state==RealtimeAxisState::ErrorStop,"lost timing did not latch/stop enabled axis");
 check(!axis.apply_command(enable),"enable allowed without timing");
 RealtimeCommand reset;reset.kind=RealtimeCommandKind::Reset;
 check(!axis.apply_command(reset),"reset allowed while timing invalid");
 axis.set_motion_timing_ready(true);axis.process(image,s);
 check(s.motion_timing_fault && !axis.apply_command(enable),"timing fault auto recovered without reset");
 check(axis.apply_command(reset),"healthy explicit timing reset rejected");axis.process(image,s);
 check(!s.motion_timing_fault && s.motion_timing_ready,"timing reset did not clear latch");
 RealtimeExchange exchange;RealtimeSnapshot published;published.axis_count=1;published.axes[0]=s;published.axes[0].motion_timing_ready=false;published.axes[0].motion_timing_fault=true;
 published.axes[0].demand_position_error=.001;published.axes[0].drive_following_error=-.002;
 published.axes[0].demand_delivery_error=.003;published.axes[0].demand_feedback_valid=true;
 published.axes[0].demand_delivery_mismatch=true;published.axes[0].demand_delivery_fault=true;
 exchange.publish(published);const auto observed=exchange.snapshot();check(!observed.axes[0].motion_timing_ready && observed.axes[0].motion_timing_fault,"timing flags lost across snapshot exchange");
 check(observed.axes[0].demand_position_error==.001 && observed.axes[0].drive_following_error==-.002
   && observed.axes[0].demand_delivery_error==.003 && observed.axes[0].demand_feedback_valid
   && observed.axes[0].demand_delivery_mismatch && observed.axes[0].demand_delivery_fault,"demand diagnostics lost across snapshot exchange");
 auto bad=c;bad.velocity_feedback_raw_per_unit_per_second=std::numeric_limits<double>::quiet_NaN();bool rejected=false;try{Cia402AxisAdapter x(layout,bad);}catch(const std::invalid_argument&){rejected=true;}check(rejected,"NaN velocity scale accepted");
 bad=c;bad.velocity_offset_raw_per_unit_per_second=-1;rejected=false;try{Cia402AxisAdapter x(layout,bad);}catch(const std::invalid_argument&){rejected=true;}check(rejected,"negative offset scale accepted");
 std::cout<<"motion correction PASS\n";return 0;
 }catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}

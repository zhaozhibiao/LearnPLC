#include "axis_feedback.hpp"
#include <nlohmann/json.hpp>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <unistd.h>
using namespace ecmotion;
void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
struct Rig {
 PdoLayout layout;
 std::vector<std::uint8_t> bytes;
 PdoImageView image;
 Cia402AxisAdapter axis;
 RealtimeAxisSnapshot s;
 Rig(AxisFeedbackConfiguration c):layout({
 {c.controlword_key,PdoDirection::ControllerOutput,0,0,16},{c.target_position_key,PdoDirection::ControllerOutput,2,0,32},
 {c.velocity_offset_key,PdoDirection::ControllerOutput,6,0,32},{c.mode_of_operation_key,PdoDirection::ControllerOutput,10,0,8},
 {c.statusword_key,PdoDirection::ControllerInput,11,0,16},{c.position_actual_key,PdoDirection::ControllerInput,13,0,32},
 {c.velocity_actual_key,PdoDirection::ControllerInput,17,0,32},{c.mode_display_key,PdoDirection::ControllerInput,21,0,8},
 {c.error_code_key,PdoDirection::ControllerInput,22,0,16},
 {"Axis1Drive.Position_demand.0x6062:0",PdoDirection::ControllerInput,24,0,32},
 {"Axis1Drive.Following_error.0x60F4:0",PdoDirection::ControllerInput,28,0,32}},32),bytes(32),image(bytes,layout),axis(layout,c){}
 void device(const char* key,std::int32_t v){image.write_from_device(layout.resolve(key),static_cast<std::uint32_t>(v));}
 void tick(std::int32_t target,std::int32_t demand,std::int32_t actual,std::int32_t error=0){
  image.write_from_controller(layout.resolve("Axis1Drive.Target_position.0x607A:0"),static_cast<std::uint32_t>(target));
  device("Axis1Drive.Position_demand.0x6062:0",demand);device("Axis1Drive.Position_actual_value.0x6064:0",actual);device("Axis1Drive.Following_error.0x60F4:0",error);axis.process(image,s);
 }
 bool command(RealtimeCommandKind k){RealtimeCommand c;c.kind=k;return axis.apply_command(c);}
 void home(){device("Axis1Drive.Statusword.0x6041:0",0x27);device("Axis1Drive.Modes_of_operation_display.0x6061:0",8);tick(0,0,0);require(command(RealtimeCommandKind::Enable),"enable failed");tick(0,0,0);require(command(RealtimeCommandKind::Home),"home start failed");device("Axis1Drive.Modes_of_operation_display.0x6061:0",6);tick(0,0,0);tick(0,0,0);device("Axis1Drive.Statusword.0x6041:0",0x1427);tick(0,0,0);device("Axis1Drive.Modes_of_operation_display.0x6061:0",8);tick(0,0,0);require(s.homed,"home lifecycle failed");}
};
int main(int argc,char**argv){try{
 require(argc==2,"fixture required");nlohmann::json root;{std::ifstream in(argv[1]);in>>root;}
 root["axes"][0]["driveDemandProtection"]=true;
 const auto path=std::filesystem::temp_directory_path()/("demand-protection-"+std::to_string(getpid())+".json");
 auto load=[&](){std::ofstream out(path);out<<root;out.close();return AxisFeedbackConfiguration::load_all(path.string()).at(0);};
 auto c=load();c.motion.soft_limit_negative=c.safety.soft_limit_negative=-10;c.motion.soft_limit_positive=c.safety.soft_limit_positive=10;
 Rig good(c);good.home();
 // Entry target is prior publication: demand equals entry history[n-2], not [n-3].
 for(int n=1;n<=50;++n){const auto demand=n>=3?(n-2)*3000:0;good.tick(n*3000,demand,demand);require(!good.s.following_error_fault && good.s.state!=RealtimeAxisState::ErrorStop,"exact lag2 healthy drive tripped raw following protection");}
 // Integrated publication loop: only process writes 607A after homing.
 auto trajectory_config=c;trajectory_config.motion.maximum_velocity=5;trajectory_config.motion.maximum_acceleration=100;trajectory_config.motion.maximum_deceleration=100;trajectory_config.safety.maximum_actual_velocity=10;
 Rig trajectory(trajectory_config);trajectory.home();RealtimeCommand move;move.kind=RealtimeCommandKind::MoveAbsolute;move.sequence=77;move.position=.5;move.velocity=5;require(trajectory.axis.apply_command(move),"integrated move rejected");
 std::array<std::int32_t,3> published{};unsigned publication=0;std::int32_t previous_demand=0;bool saw_large_raw_error=false;
 auto publication_tick=[&](bool corrupt){auto demand=published[publication];if(corrupt)++demand;
  trajectory.device("Axis1Drive.Position_demand.0x6062:0",demand);trajectory.device("Axis1Drive.Position_actual_value.0x6064:0",demand);
  trajectory.device("Axis1Drive.Velocity_actual_value.0x606C:0",(demand-previous_demand)*1000);previous_demand=demand;
  trajectory.device("Axis1Drive.Following_error.0x60F4:0",0);trajectory.axis.process(trajectory.image,trajectory.s);
  published[publication]=static_cast<std::int32_t>(trajectory.image.read_signed(trajectory.layout.resolve("Axis1Drive.Target_position.0x607A:0")));publication=(publication+1)%published.size();
 };
 for(int n=0;n<3000;++n){publication_tick(false);saw_large_raw_error|=std::abs(trajectory.s.cycle_error)>.005;require(!trajectory.s.following_error_fault && trajectory.s.state!=RealtimeAxisState::ErrorStop,"actual generated publication lag3 tripped protection");}
 require(saw_large_raw_error && trajectory.s.motion_done && !trajectory.s.motion_busy,"integrated trajectory did not prove raw error and completion");
 publication_tick(true);require(trajectory.s.following_error_fault && trajectory.s.state==RealtimeAxisState::ErrorStop,"integrated demand corruption failed to latch");
 Rig changed(c);changed.home();for(int n=1;n<=5;++n){int d=n>=3?(n-2)*100:0;changed.tick(n*100,d,d);}changed.tick(600,200,200);
 require(changed.s.state==RealtimeAxisState::ErrorStop && changed.s.following_error_fault,"changed command-to-demand lag did not latch");
 changed.tick(0,0,0);require(!changed.command(RealtimeCommandKind::Enable),"flow fault auto resumed");require(changed.command(RealtimeCommandKind::Reset),"healthy explicit reset rejected");changed.tick(0,0,0);require(changed.s.state!=RealtimeAxisState::ErrorStop,"reset did not clear flow fault");
 Rig mode(c);mode.home();mode.device("Axis1Drive.Modes_of_operation_display.0x6061:0",9);mode.tick(0,0,0);require(mode.s.following_error_fault && mode.s.state==RealtimeAxisState::ErrorStop,"unexpected CSP mode did not stop");require(!mode.command(RealtimeCommandKind::Reset),"reset accepted bad mode");
 Rig stopped(c);stopped.home();for(int n=1;n<=8;++n){int d=n>=3?(n-2)*100:0;stopped.tick(n*100,d,d);}require(stopped.command(RealtimeCommandKind::Stop),"stop rejected");stopped.tick(900,700,700);require(stopped.s.homed && !stopped.s.following_error_fault,"stop discarded homing or lag history");
 stopped.axis.set_motion_timing_ready(false);stopped.tick(0,0,0);stopped.tick(0,0,0);require(stopped.s.motion_timing_fault,"timing invalidation did not latch");stopped.axis.set_motion_timing_ready(true);stopped.tick(0,0,0);require(!stopped.command(RealtimeCommandKind::Enable),"timing fault auto resumed");require(stopped.command(RealtimeCommandKind::Reset),"healthy timing reset rejected");stopped.tick(0,0,0);stopped.home();stopped.tick(0,0,0);require(!stopped.s.following_error_fault,"timing safe-path duplicate contaminated new epoch");
 Rig unhomed(c);unhomed.device("Axis1Drive.Statusword.0x6041:0",0x27);unhomed.device("Axis1Drive.Modes_of_operation_display.0x6061:0",8);unhomed.tick(0,0,0);require(unhomed.command(RealtimeCommandKind::Enable),"unhomed enable failed");for(int n=1;n<=25;++n)unhomed.tick(20000,0,0);require(unhomed.s.following_error_fault,"unhomed raw fallback disabled");
 Rig overspeed(c);overspeed.home();overspeed.device("Axis1Drive.Velocity_actual_value.0x606C:0",1048576);overspeed.tick(0,0,0);require(overspeed.s.overspeed_fault && overspeed.s.state==RealtimeAxisState::ErrorStop,"drive reference disabled independent overspeed");
 Rig limit(c);limit.home();limit.tick(0,11534336,11534336);require(limit.s.software_limit_violation,"drive reference disabled referenced actual-position limits");
 Rig driver(c);driver.home();driver.device("Axis1Drive.Error_code.0x603F:0",0x1234);driver.tick(0,0,0);require(driver.s.state==RealtimeAxisState::ErrorStop && !driver.s.homed,"driver error lost independent gate");
 Rig stall(c);stall.home();for(int n=1;n<=30;++n){int d=n>=3?(n-2)*1000:0;stall.tick(n*1000,d,0);}
 require(stall.s.following_error_fault,"6062-to-6064 stall not protected");
 Rig error(c);error.home();for(int n=0;n<22;++n)error.tick(0,0,0,6000);require(error.s.following_error_fault,"60F4 independent error not protected");
 auto saved=root;
 root["axes"][0].erase("driveDemandProtection");auto legacy_config=load();legacy_config.motion.soft_limit_negative=legacy_config.safety.soft_limit_negative=-10;legacy_config.motion.soft_limit_positive=legacy_config.safety.soft_limit_positive=10;
 Rig legacy(legacy_config);legacy.home();for(int n=1;n<=50;++n){const auto d=n>=3?(n-2)*3000:0;legacy.tick(n*3000,d,d);}require(legacy.s.following_error_fault,"default-off lost raw following protection");root=saved;root["axes"][0]["mechanics"]["direction"]=-1;bool rejected=false;try{static_cast<void>(load());}catch(const std::invalid_argument&){rejected=true;}require(rejected,"unsupported protection direction accepted");root=saved;
 root["axes"][0]["mechanics"]["encoderCountsPerMotorRevolution"]=1000000;rejected=false;try{static_cast<void>(load());}catch(const std::invalid_argument&){rejected=true;}require(rejected,"unsupported position scale accepted");root=saved;
 root["cycleTimeMicroseconds"]=2000;rejected=false;try{static_cast<void>(load());}catch(const std::invalid_argument&){rejected=true;}require(rejected,"unsupported protection cycle accepted");root=saved;
 root["axes"][0]["driveDemandProtection"]="true";rejected=false;try{static_cast<void>(load());}catch(const std::exception&){rejected=true;}require(rejected,"nonboolean opt-in accepted");std::filesystem::remove(path);
 std::cout<<"drive demand protection PASS\n";return 0;
 }catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}


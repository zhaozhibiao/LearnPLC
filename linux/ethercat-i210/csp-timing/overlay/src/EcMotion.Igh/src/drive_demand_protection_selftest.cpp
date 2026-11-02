#include "axis_feedback.hpp"
#include <nlohmann/json.hpp>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <unistd.h>
using namespace ecmotion;
void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
struct Rig {
 AxisFeedbackConfiguration config;
 PdoLayout layout;
 std::vector<std::uint8_t> bytes;
 PdoImageView image;
 Cia402AxisAdapter axis;
 RealtimeAxisSnapshot s;
 Rig(AxisFeedbackConfiguration c):config(c),layout({
 {c.controlword_key,PdoDirection::ControllerOutput,0,0,16},{c.target_position_key,PdoDirection::ControllerOutput,2,0,32},
 {c.velocity_offset_key,PdoDirection::ControllerOutput,6,0,32},{c.mode_of_operation_key,PdoDirection::ControllerOutput,10,0,8},
 {c.statusword_key,PdoDirection::ControllerInput,11,0,16},{c.position_actual_key,PdoDirection::ControllerInput,13,0,32},
 {c.velocity_actual_key,PdoDirection::ControllerInput,17,0,32},{c.mode_display_key,PdoDirection::ControllerInput,21,0,8},
 {c.error_code_key,PdoDirection::ControllerInput,22,0,16},
 {c.position_demand_key,PdoDirection::ControllerInput,24,0,32},
 {c.following_error_key,PdoDirection::ControllerInput,28,0,32}},32),bytes(32),image(bytes,layout),axis(layout,c){}
 void device(const char* key,std::int32_t v){image.write_from_device(layout.resolve(key),static_cast<std::uint32_t>(v));}
 void tick(std::int32_t target,std::int32_t demand,std::int32_t actual,std::int32_t error=0){
  image.write_from_controller(layout.resolve(config.target_position_key),static_cast<std::uint32_t>(target));
  device(config.position_demand_key.c_str(),demand);device(config.position_actual_key.c_str(),actual);device(config.following_error_key.c_str(),error);axis.process(image,s);
 }
 bool command(RealtimeCommandKind k){RealtimeCommand c;c.kind=k;return axis.apply_command(c);}
 void home(){device(config.statusword_key.c_str(),0x27);device(config.mode_display_key.c_str(),8);tick(0,0,0);require(command(RealtimeCommandKind::Enable),"enable failed");tick(0,0,0);require(command(RealtimeCommandKind::Home),"home start failed");device(config.mode_display_key.c_str(),6);tick(0,0,0);tick(0,0,0);device(config.statusword_key.c_str(),0x1427);tick(0,0,0);device(config.mode_display_key.c_str(),8);tick(0,0,0);require(s.homed,"home lifecycle failed");}
};
int main(int argc,char**argv){try{
 require(argc==2,"fixture required");nlohmann::json root;{std::ifstream in(argv[1]);in>>root;}
 root["axes"][0]["driveDemandProtection"]=true;
 root["axes"][0]["demandDeliveryEnforced"]=true; // Explicit diagnostic enforcement for this regression.
 root["axes"][0]["pdoEntries"]["positionDemand"]="Axis1Drive.Position_demand.0x6062:0";
 root["axes"][0]["pdoEntries"]["followingError"]="Axis1Drive.Following_error.0x60F4:0";
 root["axes"][0]["positionDemandRawPerUnit"]=1048576.0;
 root["axes"][0]["followingErrorRawPerUnit"]=1048576.0;
 const auto path=std::filesystem::temp_directory_path()/("demand-protection-"+std::to_string(getpid())+".json");
 auto load=[&](){std::ofstream out(path);out<<root;out.close();return AxisFeedbackConfiguration::load_all(path.string()).at(0);};
 auto c=load();c.motion.soft_limit_negative=c.safety.soft_limit_negative=-10;c.motion.soft_limit_positive=c.safety.soft_limit_positive=10;
 // Dynamic requests must have exactly the same admission result at precheck/apply.
 auto dynamic_config=c;dynamic_config.motion.maximum_jerk=20;dynamic_config.motion.profile_defaults_known=true;
 Rig dynamic(dynamic_config);dynamic.home();
 RealtimeCommand request;request.kind=RealtimeCommandKind::MoveAbsolute;request.motion_profile_version=2;
 request.position=.01;request.velocity=.01;request.acceleration=dynamic_config.motion.maximum_acceleration+1;
 require(!dynamic.axis.can_apply_command(request) && !dynamic.axis.apply_command(request),"dynamic above-max mismatch");
 request.acceleration=0;request.deceleration=0;request.jerk=0;
 require(dynamic.axis.can_apply_command(request) && dynamic.axis.apply_command(request),"dynamic inherited move mismatch");
 request.position=.02;
 require(!dynamic.axis.can_apply_command(request) && !dynamic.axis.apply_command(request),"Busy move mutated dynamic trajectory");
 RealtimeCommand ordinary;ordinary.kind=RealtimeCommandKind::Stop;ordinary.motion_profile_version=2;ordinary.deceleration=-1;
 require(!dynamic.axis.can_apply_command(ordinary) && !dynamic.axis.apply_command(ordinary),"invalid ordinary Stop accepted");
 require(dynamic.command(RealtimeCommandKind::Disable),"protective Disable was blocked by dynamic profile");
 auto unknown_defaults=dynamic_config;unknown_defaults.motion.profile_defaults_known=false;
 Rig unknown_dynamic(unknown_defaults);unknown_dynamic.home();
 require(!unknown_dynamic.axis.can_apply_command(request) && !unknown_dynamic.axis.apply_command(request),"unknown dynamic defaults accepted");
 Rig good(c);good.home();
 auto auxiliary_config=c;auxiliary_config.drive_error_only=true;auxiliary_config.demand_delivery_enforced=false;
 Rig auxiliary(auxiliary_config);auxiliary.home();
 for(int n=0;n<50;++n){auxiliary.tick(15000,9000,0,0);require(!auxiliary.s.following_error_fault,"diagnostic delivery mismatch stopped auxiliary mode");if(n>2)require(auxiliary.s.demand_delivery_mismatch && !auxiliary.s.demand_delivery_fault,"diagnostic mismatch not separately reported");}
 for(int n=0;n<19;++n){auxiliary.tick(15000,9000,0,6000);require(!auxiliary.s.following_error_fault,"drive-error timeout fired early");}
 auxiliary.tick(15000,9000,0,6000);require(auxiliary.s.following_error_fault,"drive-error auxiliary failed to trip at timeout");
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
 publication_tick(true);require(trajectory.s.following_error_fault && trajectory.s.demand_delivery_fault && trajectory.s.state==RealtimeAxisState::ErrorStop,"integrated demand corruption failed to latch");
 Rig changed(c);changed.home();for(int n=1;n<=5;++n){int d=n>=3?(n-2)*100:0;changed.tick(n*100,d,d);}changed.tick(600,200,200);
 require(changed.s.state==RealtimeAxisState::ErrorStop && changed.s.following_error_fault,"changed command-to-demand lag did not latch");
 changed.tick(0,0,0);require(!changed.command(RealtimeCommandKind::Enable),"flow fault auto resumed");require(changed.command(RealtimeCommandKind::Reset),"healthy explicit reset rejected");changed.tick(0,0,0);require(changed.s.state!=RealtimeAxisState::ErrorStop,"reset did not clear flow fault");
 Rig mode(c);mode.home();mode.device("Axis1Drive.Modes_of_operation_display.0x6061:0",9);mode.tick(0,0,0);require(mode.s.following_error_fault && mode.s.state==RealtimeAxisState::ErrorStop,"unexpected CSP mode did not stop");require(!mode.command(RealtimeCommandKind::Reset),"reset accepted bad mode");
 Rig stopped(c);stopped.home();for(int n=1;n<=8;++n){int d=n>=3?(n-2)*100:0;stopped.tick(n*100,d,d);}require(stopped.command(RealtimeCommandKind::Stop),"stop rejected");stopped.tick(900,700,700);require(stopped.s.homed && !stopped.s.following_error_fault,"stop discarded homing or lag history");
 stopped.axis.set_motion_timing_ready(false);stopped.tick(0,0,0);stopped.tick(0,0,0);require(stopped.s.motion_timing_fault,"timing invalidation did not latch");stopped.axis.set_motion_timing_ready(true);stopped.tick(0,0,0);require(!stopped.command(RealtimeCommandKind::Enable),"timing fault auto resumed");require(stopped.command(RealtimeCommandKind::Reset),"healthy timing reset rejected");stopped.tick(0,0,0);stopped.home();stopped.tick(0,0,0);require(!stopped.s.following_error_fault,"timing safe-path duplicate contaminated new epoch");
 // Smooth Stop/Disable/fault interruption clears Busy and never reports a false Done.
 auto smooth_config=c;smooth_config.motion.maximum_jerk=1;
 Rig interrupted(smooth_config);interrupted.home();RealtimeCommand smooth_move;
 smooth_move.kind=RealtimeCommandKind::MoveAbsolute;smooth_move.sequence=99;smooth_move.position=.01;smooth_move.velocity=.01;
 require(interrupted.axis.apply_command(smooth_move),"smooth move rejected");interrupted.tick(0,0,0);
 smooth_move.position=-.01;require(!interrupted.axis.apply_command(smooth_move),"moving reversal accepted");
 require(interrupted.command(RealtimeCommandKind::Stop),"smooth stop rejected");interrupted.tick(0,0,0);
 require(!interrupted.s.motion_busy && interrupted.s.motion_aborted && !interrupted.s.motion_done,"smooth stop completion incorrect");
 require(interrupted.command(RealtimeCommandKind::Disable),"smooth disable rejected");interrupted.tick(0,0,0);
 require(!interrupted.s.homed && !interrupted.s.motion_busy && !interrupted.s.motion_done,"disable retained completion/reference");
 require(interrupted.image.read_signed(interrupted.layout.resolve(c.target_position_key))==0,"disable retained old curve");
 Rig unhomed(c);unhomed.device("Axis1Drive.Statusword.0x6041:0",0x27);unhomed.device("Axis1Drive.Modes_of_operation_display.0x6061:0",8);unhomed.tick(0,0,0);require(unhomed.command(RealtimeCommandKind::Enable),"unhomed enable failed");for(int n=1;n<=25;++n)unhomed.tick(20000,0,0);require(unhomed.s.following_error_fault,"unhomed raw fallback disabled");
 Rig overspeed(c);overspeed.home();overspeed.device("Axis1Drive.Velocity_actual_value.0x606C:0",1048576);overspeed.tick(0,0,0);require(overspeed.s.overspeed_fault && overspeed.s.state==RealtimeAxisState::ErrorStop,"drive reference disabled independent overspeed");
 Rig limit(c);limit.home();limit.tick(0,11534336,11534336);require(limit.s.software_limit_violation,"drive reference disabled referenced actual-position limits");
 Rig driver(c);driver.home();driver.device("Axis1Drive.Error_code.0x603F:0",0x1234);driver.tick(0,0,0);require(driver.s.state==RealtimeAxisState::ErrorStop && !driver.s.homed,"driver error lost independent gate");
 // 603F remains latched until the drive observes a fault-reset pulse.
 Rig reset_drive(c);reset_drive.home();
 reset_drive.device(c.statusword_key.c_str(),0x08);reset_drive.device(c.error_code_key.c_str(),0x1234);
 reset_drive.tick(0,0,0);require(!reset_drive.command(RealtimeCommandKind::Enable),"driver fault allowed enable");
 RealtimeCommand reset_request;reset_request.kind=RealtimeCommandKind::Reset;
 require(reset_drive.axis.can_apply_command(reset_request),"stable disabled drive fault cannot begin explicit reset");
 require(reset_drive.command(RealtimeCommandKind::Reset),"explicit driver reset rejected while 603F latched");
 reset_drive.tick(0,0,0);
 require(reset_drive.image.read_unsigned(reset_drive.layout.resolve(c.controlword_key))==0x80,"driver reset pulse was not sent");
 require(reset_drive.s.state==RealtimeAxisState::ErrorStop && !reset_drive.s.homed,"driver reset completed before drive acknowledgment");
 reset_drive.tick(0,0,0);
 require(reset_drive.image.read_unsigned(reset_drive.layout.resolve(c.controlword_key))==0,"driver reset pulse repeated or enabled drive");
 require(reset_drive.s.state==RealtimeAxisState::ErrorStop,"uncleared 603F falsely completed reset");
 reset_drive.device(c.error_code_key.c_str(),0);reset_drive.tick(0,0,0);
 require(reset_drive.s.state==RealtimeAxisState::ErrorStop,"uncleared fault status falsely completed reset");
 reset_drive.device(c.statusword_key.c_str(),0x40);reset_drive.tick(0,0,0);
 require(reset_drive.s.state==RealtimeAxisState::Disabled && !reset_drive.s.homed && !reset_drive.s.motion_busy
  && !reset_drive.s.motion_done && reset_drive.image.read_unsigned(reset_drive.layout.resolve(c.controlword_key))==0,
  "single explicit reset did not finish disabled after acknowledgment");
 // Faulted homing mode may require a reset before the drive accepts CSP mode.
 Rig reset_mode(c);reset_mode.home();reset_mode.device(c.mode_display_key.c_str(),6);
 reset_mode.device(c.statusword_key.c_str(),0x08);reset_mode.device(c.error_code_key.c_str(),0x1234);reset_mode.tick(0,0,0);
 require(reset_mode.axis.can_apply_command(reset_request) && reset_mode.command(RealtimeCommandKind::Reset),"faulted homing mode prevented safe driver-reset pulse");
 reset_mode.tick(0,0,0);require(reset_mode.image.read_unsigned(reset_mode.layout.resolve(c.controlword_key))==0x80,"mode6 reset pulse missing");
 reset_mode.device(c.statusword_key.c_str(),0x40);reset_mode.device(c.error_code_key.c_str(),0);reset_mode.tick(0,0,0);
 require(reset_mode.s.state==RealtimeAxisState::ErrorStop && !reset_mode.command(RealtimeCommandKind::Enable)
  && !reset_mode.axis.can_apply_command(reset_request),"mode6 acknowledgment prematurely cleared reset gate");
 reset_mode.device(c.mode_display_key.c_str(),8);reset_mode.tick(0,0,0);
 require(reset_mode.s.state==RealtimeAxisState::Disabled && !reset_mode.s.homed
  && reset_mode.image.read_unsigned(reset_mode.layout.resolve(c.controlword_key))==0,"mode8 acknowledgment did not finish disabled");
 RealtimeCommand enable_request;enable_request.kind=RealtimeCommandKind::Enable;
 require(reset_mode.axis.can_apply_command(enable_request),"healthy mode8 explicit re-enable stayed blocked");
 Rig enable_gate(c);enable_gate.device(c.statusword_key.c_str(),0x40);enable_gate.device(c.mode_display_key.c_str(),9);enable_gate.tick(0,0,0);
 require(!enable_gate.axis.can_apply_command(enable_request) && !enable_gate.command(RealtimeCommandKind::Enable),"wrong mode permitted fresh protected Enable");
 enable_gate.device(c.mode_display_key.c_str(),8);enable_gate.tick(0,0,0,6000);
 require(!enable_gate.axis.can_apply_command(enable_request) && !enable_gate.command(RealtimeCommandKind::Enable),"unhealthy demand permitted fresh protected Enable");
 enable_gate.tick(0,0,0);require(enable_gate.axis.can_apply_command(enable_request),"healthy protected Enable remained blocked");
 // A drive-reset pulse must not clear unhealthy host following-error protection.
 Rig reset_host(c);reset_host.home();for(int n=0;n<22;++n)reset_host.tick(0,0,0,6000);
 require(reset_host.s.following_error_fault,"host following-error setup did not trip");
 reset_host.device(c.statusword_key.c_str(),0x08);reset_host.device(c.error_code_key.c_str(),0x1234);reset_host.tick(0,0,0,6000);
 require(reset_host.command(RealtimeCommandKind::Reset),"host fault prevented safe drive reset pulse");reset_host.tick(0,0,0,6000);
 require(reset_host.image.read_unsigned(reset_host.layout.resolve(c.controlword_key))==0x80 && reset_host.s.following_error_fault,
  "driver reset erased host latch before recovery");
 reset_host.device(c.statusword_key.c_str(),0x40);reset_host.device(c.error_code_key.c_str(),0);reset_host.tick(0,0,0,6000);
 require(reset_host.s.following_error_fault && reset_host.s.state==RealtimeAxisState::ErrorStop,"unhealthy host following error cleared");
 reset_host.tick(0,0,0);require(!reset_host.s.following_error_fault && reset_host.s.state==RealtimeAxisState::Disabled,
  "healthy host protection did not finish explicit reset");
 Rig reset_bounds(c);reset_bounds.home();reset_bounds.device(c.statusword_key.c_str(),0x08);reset_bounds.device(c.error_code_key.c_str(),0x1234);
 reset_bounds.tick(0,0,11534336);require(!reset_bounds.command(RealtimeCommandKind::Reset),"out-of-bound feedback permitted drive reset");
 Rig reset_moving(c);reset_moving.home();reset_moving.device(c.statusword_key.c_str(),0x08);reset_moving.device(c.error_code_key.c_str(),0x1234);
 reset_moving.device(c.velocity_actual_key.c_str(),1048576);reset_moving.tick(0,0,0);
 require(!reset_moving.command(RealtimeCommandKind::Reset),"moving feedback permitted drive reset");
 // A driver error must also drop an enabled old trajectory immediately.
 auto driver_motion_config=c;driver_motion_config.motion.maximum_jerk=1;
 Rig driver_motion(driver_motion_config);driver_motion.home();RealtimeCommand driver_move;
 driver_move.kind=RealtimeCommandKind::MoveAbsolute;driver_move.position=.01;driver_move.velocity=.01;driver_move.sequence=120;
 require(driver_motion.axis.apply_command(driver_move),"driver motion setup rejected");
 for(int n=0;n<10;++n)driver_motion.tick(0,0,0);
 driver_motion.device(c.error_code_key.c_str(),0x1234);driver_motion.tick(0,0,0);
 require(!driver_motion.s.motion_busy && driver_motion.s.motion_aborted && !driver_motion.s.motion_done
  && driver_motion.s.state==RealtimeAxisState::ErrorStop,"driver error did not abort active motion");
 require(driver_motion.image.read_signed(driver_motion.layout.resolve(c.target_position_key))==0
  && driver_motion.image.read_unsigned(driver_motion.layout.resolve(c.controlword_key))!=0x0f,
  "driver error retained enabled old trajectory");
 Rig stall(c);stall.home();for(int n=1;n<=30;++n){int d=n>=3?(n-2)*1000:0;stall.tick(n*1000,d,0);}
 require(stall.s.following_error_fault,"6062-to-6064 stall not protected");
 Rig error(c);error.home();for(int n=0;n<22;++n)error.tick(0,0,0,6000);require(error.s.following_error_fault,"60F4 independent error not protected");
 auto saved=root;
 root["axes"][0].erase("driveDemandProtection");auto legacy_config=load();legacy_config.motion.soft_limit_negative=legacy_config.safety.soft_limit_negative=-10;legacy_config.motion.soft_limit_positive=legacy_config.safety.soft_limit_positive=10;
 Rig legacy(legacy_config);legacy.home();for(int n=1;n<=50;++n){const auto d=n>=3?(n-2)*3000:0;legacy.tick(n*3000,d,d);}require(legacy.s.following_error_fault,"default-off lost raw following protection");root=saved;
 // Formal engineering units, direction, scales and cycle are not bench constants.
 root["axes"][0]["mechanics"]["direction"]=-1;
 root["axes"][0]["mechanics"]["encoderCountsPerMotorRevolution"]=1000000;
 root["cycleTimeMicroseconds"]=2000;root["axes"][0]["positionUnit"]="mm";
 root["axes"][0]["pdoEntries"]["positionActual"]="Machine.X.actual";
 root["axes"][0]["pdoEntries"]["positionDemand"]="Machine.X.demand";
 root["axes"][0]["pdoEntries"]["followingError"]="Machine.X.following";
 root["axes"][0]["demandDeliveryTolerance"]=.25;
 auto generic=load();require(generic.position_demand_key=="Machine.X.demand" && generic.following_error_key=="Machine.X.following","project bindings lost");
 generic.position_demand_raw_per_unit=10000;generic.following_error_raw_per_unit=20000;
 generic.motion.soft_limit_negative=generic.safety.soft_limit_negative=-10;
 generic.motion.soft_limit_positive=generic.safety.soft_limit_positive=10;
 generic.drive_error_only=true;generic.demand_delivery_enforced=false;
 Rig units(generic);units.home();
 for(int n=0;n<9;++n){units.tick(0,0,0,120);require(!units.s.following_error_fault,"generic timeout fired early");}
 units.tick(0,0,0,120);require(units.s.following_error_fault,"generic 60F4 units or 2ms timeout ignored");
 auto scaled_config=generic;scaled_config.drive_error_only=false;
 Rig scaled(scaled_config);scaled.home();
 for(int n=0;n<30;++n){scaled.tick(10000,100,10000);require(!scaled.s.following_error_fault,"different demand/encoder scales falsely tripped");}
 for(int n=0;n<9;++n){scaled.tick(10000,100,0);require(!scaled.s.following_error_fault,"scaled stall timeout fired early");}
 scaled.tick(10000,100,0);require(scaled.s.following_error_fault,"scaled actual feedback freeze not protected");
 auto unknown=generic;unknown.following_error_raw_per_unit=0;bool rejected=false;
 try{Rig bad(unknown);}catch(const std::invalid_argument&){rejected=true;}require(rejected,"unknown following-error units accepted");
 unknown=generic;unknown.position_demand_key="";rejected=false;
 try{Rig bad(unknown);}catch(const std::invalid_argument&){rejected=true;}require(rejected,"missing demand binding accepted");
 unknown=generic;unknown.motion.soft_limit_positive=1e20;rejected=false;
 try{Rig bad(unknown);}catch(const std::invalid_argument&){rejected=true;}require(rejected,"unreachable int32 travel accepted");
 unknown=generic;unknown.following_error_key=unknown.position_actual_key;rejected=false;
 try{Cia402AxisAdapter bad(units.layout,unknown);}catch(const std::invalid_argument&){rejected=true;}require(rejected,"following-error alias to position accepted");
 auto missing_binding=generic;missing_binding.position_demand_key="absent";rejected=false;
 try{Cia402AxisAdapter bad(units.layout,missing_binding);}catch(const std::exception&){rejected=true;}
 require(rejected,"unmapped demand PDO accepted");
 unknown=generic;unknown.following_error_raw_per_unit=std::numeric_limits<double>::infinity();rejected=false;
 try{Rig bad(unknown);}catch(const std::invalid_argument&){rejected=true;}require(rejected,"infinite following-error units accepted");
 root=saved;root["axes"][0].erase("demandDeliveryEnforced");
 require(!load().demand_delivery_enforced,"new protection default unexpectedly enforces fixed-lag diagnostic");
 root=saved;root["axes"][0].erase("followingErrorRawPerUnit");rejected=false;
 try{static_cast<void>(load());}catch(const std::invalid_argument&){rejected=true;}require(rejected,"missing 60F4 units accepted");root=saved;
 root["axes"][0]["driveDemandProtection"]="true";rejected=false;try{static_cast<void>(load());}catch(const std::exception&){rejected=true;}require(rejected,"nonboolean opt-in accepted");std::filesystem::remove(path);
 std::cout<<"drive demand protection PASS\n";return 0;
 }catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}


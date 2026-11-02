// SPDX-License-Identifier: MIT
// Actual compiler-generated function blocks; no controller or device IO.
#include "generated.hpp"
#include "axis_bridge.hpp"
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>
using namespace strucpp;
extern workbench_axis::FeedbackPacket axis_fixture_feedback;
extern bool axis_fixture_feedback_ready;
static void check(bool value,const char* text){if(!value)throw std::runtime_error(text);}
static void finish(unsigned axis,std::uint64_t request){AB_FEEDBACKREQUEST[axis]=request;AB_BUSY[axis]=false;AB_DONE[axis]=true;AB_ERROR[axis]=0;AB_ABORTED[axis]=false;AB_STATE[axis]=2;AB_ENABLED[axis]=true;AB_HOMED[axis]=true;AB_MOTIONID[axis]=42;AB_ACTUALVELOCITY[axis]=0;}
int main(int argc,char** argv){try{
 check(argc==2,"scenario required");std::string mode=argv[1];
 AB_STATE[1]=2;AB_ENABLED[1]=true;AB_HOMED[1]=true;
 MC_MOVEABSOLUTE move;move.AXIS=1;move.POSITION=1;move.VELOCITY=0.5;
 if(mode=="active-ack"){
  move.EXECUTE=true;move();check(move.BUSY&&!move.ACTIVE,"queued but unacknowledged operation was already Active");AB_FEEDBACKREQUEST[1]=1;AB_BUSY[1]=true;move();check(move.BUSY&&move.ACTIVE,"accepted operation did not become Active");finish(1,1);move();check(move.DONE&&!move.ACTIVE,"completed positioning operation remained Active");
 }else if(mode=="new-error-clear"){
  move.EXECUTE=true;move();AB_FEEDBACKREQUEST[1]=1;AB_ERROR[1]=2007;move();check(move.ERROR,"fixture command error missing");
  move.EXECUTE=false;move();move.EXECUTE=true;move();check(move.BUSY&&!move.ERROR&&move.ERRORID==0&&!move.DONE,"new edge inherited a previous command error");
 }else if(mode=="terminal-retained"){
  move.EXECUTE=true;move();finish(1,1);move();check(move.DONE,"completion missing");
  MC_MOVEABSOLUTE other;other.AXIS=1;other.EXECUTE=true;other.POSITION=2;other.VELOCITY=0.5;other();move();
  check(move.DONE&&!move.COMMANDABORTED&&!move.ERROR,"completed command changed into aborted when next command started");
 }else if(mode=="terminal-reset"){
  move.EXECUTE=true;move();finish(1,1);move();move.EXECUTE=false;move();move();check(!move.DONE&&!move.ERROR&&!move.COMMANDABORTED&&!move.BUSY,"terminal outputs did not clear after Execute fell");
 }else if(mode=="early-release"){
  move.EXECUTE=true;move();move.EXECUTE=false;move();check(move.BUSY,"Execute falling cancelled active motion");finish(1,1);move();check(move.DONE,"early-release completion was not visible for one invocation");move();check(!move.DONE&&!move.BUSY,"early-release completion did not reset");
 }else if(mode=="busy-retrigger"){
  move.EXECUTE=true;move();move.EXECUTE=false;move();move.EXECUTE=true;move.POSITION=9;move();check(move.BUSY&&!move.ERROR&&move.REQUESTID==1&&AB_POSITION[1]==1,"busy retrigger replaced or faulted its active command");
 }else if(mode=="aborted-reset"){
  move.EXECUTE=true;move();MC_STOP stop;stop.AXIS=1;stop.EXECUTE=true;stop();move();check(move.COMMANDABORTED&&!move.BUSY&&!move.DONE,"active preemption missing");move.EXECUTE=false;move();move();check(!move.COMMANDABORTED,"aborted result did not clear");
 }else if(mode=="stop-held"){
  MC_STOP stop;stop.AXIS=1;stop.EXECUTE=true;stop();finish(1,1);stop();check(stop.DONE&&stop.BUSY&&AB_STOPHELD[1],"held completed Stop must stay Busy and retain lock");stop.EXECUTE=false;stop();check(!stop.DONE&&!stop.BUSY&&!AB_STOPHELD[1],"Stop falling edge failed to release/reset");
 }else if(mode=="reader-command-error"||mode=="reader-axis-fault"){
  AB_STATE[1]=mode=="reader-axis-fault"?7:2;AB_ERROR[1]=mode=="reader-axis-fault"?2005:2007;
  MC_READAXISSTATUS status;status.AXIS=1;status.ENABLE=true;status();
  MC_READACTUALPOSITION position;position.AXIS=1;position.ENABLE=true;position();
  MC_READACTUALVELOCITY velocity;velocity.AXIS=1;velocity.ENABLE=true;velocity();
  check(status.VALID&&position.VALID&&velocity.VALID&&!status.ERROR&&!position.ERROR&&!velocity.ERROR,"fresh axis data was hidden by an unrelated command/drive error");
  if(mode=="reader-axis-fault")check(status.ERRORSTOP,"actual ErrorStop state was lost");
 }else if(mode=="reader-stop-lock"){
  MC_STOP stop;stop.AXIS=1;stop.EXECUTE=true;stop();finish(1,1);stop();MC_READSTATUS status;status.AXIS=1;status.ENABLE=true;status();check(status.STOPPING&&!status.STANDSTILL,"held Stop did not retain the public Stopping state");stop.EXECUTE=false;stop();status();check(status.STANDSTILL&&!status.STOPPING,"Stop release did not restore public Standstill state");
 }else if(mode=="reader-enable"){
  MC_READSTATUS status;status.AXIS=1;status.ENABLE=true;status();check(status.VALID&&status.BUSY&&status.STANDSTILL&&!status.SYNCHRONIZEDMOTION,"canonical status outputs missing");status.ENABLE=false;status();check(!status.VALID&&!status.BUSY&&!status.ERROR&&!status.STANDSTILL,"reader disable did not clear interface outputs");
 }else if(mode=="legacy-worker-failure"){
  for(auto error:{2002,2003,2004,2008}){
   auto& f=axis_fixture_feedback.axes[0];f.state=2;f.enabled=1;f.homed=1;f.error=error;f.position=9;
   axis_fixture_feedback_ready=true;workbench_axis_after_scan(true);
   MC_READSTATUS status;status.AXIS=1;status.ENABLE=true;status();
   check(!status.VALID&&status.ERROR&&!AB_ENABLED[1]&&!AB_HOMED[1],"legacy worker failure exposed stale state as valid");
  }
 }else if(mode=="reader-link-loss"){
  AB_STATE[1]=0;AB_ERROR[1]=2004;MC_READAXISSTATUS status;status.AXIS=1;status.ENABLE=true;status();check(!status.VALID&&status.ERROR&&status.ERRORID==2004,"invalid link looked healthy");
 }else if(mode=="power-fault"){
  MC_POWER power;power.AXIS=1;power.ENABLE=true;power();finish(1,1);power();AB_STATE[1]=7;AB_ERROR[1]=0;power();check(!power.STATUS&&power.ERROR&&power.ERRORID==2005,"Power hid a subsequent live drive fault");
 }else if(mode=="power-pending-enable"||mode=="power-pending-error"){
  AB_STATE[1]=1;AB_ENABLED[1]=false;MC_POWER power;power.AXIS=1;power.ENABLE=false;power();check(power.REQUESTID==1,"initial disable not submitted");power.ENABLE=true;power();check(AB_KIND[1]==2,"enable ran before prior disable was confirmed");
  AB_FEEDBACKREQUEST[1]=1;AB_STATE[1]=1;AB_ENABLED[1]=false;AB_BUSY[1]=false;AB_DONE[1]=mode=="power-pending-enable";AB_ERROR[1]=mode=="power-pending-error"?2004:0;
  power();power();
  if(mode=="power-pending-enable")check(power.REQUESTID==2&&AB_KIND[1]==1,"desired enable was lost during disable transition");
  else{power();check(AB_REQUEST[1]==1&&power.ERROR,"failed disable automatically retried enable");}
 }else if(mode=="power-live"){
  MC_POWER power;power.AXIS=1;power.ENABLE=true;power();finish(1,1);power();check(power.STATUS&&!power.BUSY,"enable did not finish");move.EXECUTE=true;move();power();check(power.STATUS&&!power.ERROR&&!power.COMMANDABORTED,"subsequent motion aborted a completed power operation");AB_STATE[1]=0;AB_ERROR[1]=2004;power();check(!power.STATUS&&power.ERROR,"Power reported enabled/healthy after link loss");
 }else if(mode=="velocity-correlated"){
  MC_MOVEVELOCITY velocity;velocity.AXIS=1;velocity.VELOCITY=0.5;velocity.EXECUTE=true;AB_ACTUALVELOCITY[1]=0.5;velocity();check(!velocity.INVELOCITY,"old actual speed proved an unacknowledged new velocity command");
  AB_FEEDBACKREQUEST[1]=1;AB_BUSY[1]=true;velocity();check(velocity.INVELOCITY,"correlated actual speed not reflected");AB_STATE[1]=0;AB_ERROR[1]=2004;velocity();check(!velocity.INVELOCITY,"stale actual speed remained InVelocity");
 }else throw std::runtime_error("unknown test");
 std::cout<<"PASS "<<mode<<'\n';
 }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}}

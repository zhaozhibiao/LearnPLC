#include "generated.hpp"
#include "axis_bridge.hpp"
#include <cassert>
#include <iostream>
#include <limits>
extern bool axis_fixture_throw;
extern workbench_axis::CommandPacket axis_fixture_last;
int main(){
 using namespace strucpp; using namespace workbench_axis;
 Mailbox<CommandPacket,3> queue;CommandPacket p{};p.axes[0].request=1;assert(queue.push(p));p.axes[0].request=2;assert(queue.push(p));assert(!queue.push(p));assert(queue.pop(p)&&p.axes[0].request==1);assert(queue.pop(p)&&p.axes[0].request==2);assert(!queue.pop(p));
 MC_MOVEABSOLUTE move{};move.AXIS=1;move.EXECUTE=true;move.POSITION=12;move.VELOCITY=2;move();assert(move.BUSY && move.REQUESTID==1);move();assert(AB_REQUEST[1]==1);
 MC_MOVEVELOCITY competing{};competing.AXIS=1;competing.EXECUTE=true;competing.VELOCITY=2;competing();assert(competing.ERRORID==2006);
 MC_STOP stop{};stop.AXIS=1;stop.EXECUTE=true;stop();assert(AB_REQUEST[1]==2 && AB_STOPHELD[1]);move();assert(move.COMMANDABORTED && !move.DONE);
 AB_STATE[1]=2;AB_ACTUALVELOCITY[1]=0;AB_FEEDBACKREQUEST[1]=2;AB_BUSY[1]=false;AB_DONE[1]=true;stop();assert(stop.DONE && AB_STOPHELD[1]);competing.EXECUTE=false;competing();competing.EXECUTE=true;competing();assert(competing.ERRORID==2006);
 stop.EXECUTE=false;stop();assert(!AB_STOPHELD[1]);
 MC_POWER power{};power.AXIS=1;power.ENABLE=true;power();assert(AB_REQUEST[1]==3 && AB_KIND[1]==1);power.ENABLE=false;power();assert(AB_REQUEST[1]==4 && AB_KIND[1]==2);
 MC_HOME home{};home.AXIS=65;home.EXECUTE=true;home();assert(home.ERRORID==2001);home.AXIS=0;home();assert(home.ERRORID==2001);
  // Disable preempts a held Stop and must release its lock.
 stop.EXECUTE=true;stop();assert(AB_STOPHELD[1]);power.ENABLE=true;power();power.ENABLE=false;power();assert(!AB_STOPHELD[1]);
  MC_MOVEABSOLUTE invalid{};invalid.AXIS=2;invalid.EXECUTE=true;invalid.POSITION=std::numeric_limits<double>::quiet_NaN();invalid.VELOCITY=1;invalid();assert(invalid.ERRORID==2007 && AB_REQUEST[2]==0);
 invalid.EXECUTE=false;invalid();invalid.EXECUTE=true;invalid.POSITION=1;invalid.VELOCITY=std::numeric_limits<double>::infinity();invalid();assert(invalid.ERRORID==2007);
 AB_REQUEST[2]=UINT64_MAX;AB_FEEDBACKREQUEST[2]=UINT64_MAX;invalid.EXECUTE=false;invalid();invalid.EXECUTE=true;invalid.VELOCITY=1;invalid();assert(invalid.ERRORID==2009);
 Configuration_CONFIG0 configuration;AB_SCANCOUNTER=0;configuration.INSTANCE0.run();assert(AB_SCANCOUNTER==1 && axis_fixture_last.flags==1);
 axis_fixture_throw=true;try{configuration.INSTANCE0.run();assert(false);}catch(int){}assert(AB_SCANCOUNTER==1 && axis_fixture_last.flags==0);axis_fixture_throw=false;
  MC_MOVEABSOLUTE proof{};proof.AXIS=3;proof.EXECUTE=true;proof.POSITION=12;proof.VELOCITY=2;proof();AB_FEEDBACKREQUEST[3]=1;AB_DONE[3]=true;AB_STATE[3]=2;AB_ACTUALPOSITION[3]=12;proof();assert(!proof.DONE && proof.ERRORID==2008);AB_MOTIONID[3]=87;proof();assert(proof.DONE && !proof.ERROR);
 MC_MOVEABSOLUTE independent{};independent.AXIS=4;independent.EXECUTE=true;independent.POSITION=1;independent.VELOCITY=1;independent();assert(independent.BUSY && independent.REQUESTID==1);
 MC_STOP owner{};owner.AXIS=4;owner.EXECUTE=true;owner();assert(AB_STOPHELD[4]);owner.AXIS=5;owner();assert(AB_STOPHELD[4] && !AB_STOPHELD[5]);owner.EXECUTE=false;owner();assert(!AB_STOPHELD[4]);
 MC_POWER initialOff{};initialOff.AXIS=6;initialOff.ENABLE=false;initialOff();assert(initialOff.REQUESTID==1 && AB_KIND[6]==2);
  MC_MOVEVELOCITY latched{};latched.AXIS=7;latched.EXECUTE=true;latched.VELOCITY=3;latched();AB_ACTUALVELOCITY[7]=3;latched.VELOCITY=2;latched();assert(latched.INVELOCITY);
 AB_ACTUALVELOCITY[7]=2;latched();assert(!latched.INVELOCITY);
 latched.AXIS=8;AB_ACTUALVELOCITY[8]=2;latched();assert(!latched.INVELOCITY);AB_ACTUALVELOCITY[7]=3;latched();assert(latched.INVELOCITY);
 latched.EXECUTE=false;latched();latched.EXECUTE=true;latched.VELOCITY=5;latched();AB_ACTUALVELOCITY[8]=5;latched();assert(latched.INVELOCITY);
 std::cout<<"mailbox ordering, preemption, held Stop, power edges and bounds passed\n";
}








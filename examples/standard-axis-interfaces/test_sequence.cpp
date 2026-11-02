// SPDX-License-Identifier: MIT
// Compiler-generated ST and a memory-only feedback model, no sockets or devices.
#include "generated.hpp"
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
using namespace strucpp;
static std::uint64_t seen=0;
static int remaining=0;
static double target=0;
static long long now_ns=0,forward_done_ns=0;
static bool disconnected=false;
static std::vector<unsigned> commands;
static std::vector<double> positions;
static std::vector<long long> move_times;
extern "C" void workbench_axis_after_scan(bool ok){
 if(!ok)throw std::runtime_error("scan failed");
 if(disconnected){AB_STATE[1]=0;AB_ERROR[1]=2004;AB_FEEDBACKREQUEST[1]=AB_REQUEST[1];AB_BUSY[1]=false;AB_DONE[1]=false;return;}
 if(remaining>0&&--remaining==0){AB_ACTUALPOSITION[1]=target;AB_ACTUALVELOCITY[1]=0;AB_STATE[1]=2;AB_BUSY[1]=false;AB_DONE[1]=true;if(positions.size()==1)forward_done_ns=now_ns;}
 auto id=static_cast<std::uint64_t>(AB_REQUEST[1]);if(id==seen)return;seen=id;
 auto kind=static_cast<unsigned>(AB_KIND[1]);commands.push_back(kind);
 AB_FEEDBACKREQUEST[1]=id;AB_BUSY[1]=false;AB_DONE[1]=true;AB_ERROR[1]=0;AB_ABORTED[1]=false;
 if(kind==1){AB_STATE[1]=2;AB_ENABLED[1]=true;}
 if(kind==2){remaining=0;AB_STATE[1]=1;AB_ENABLED[1]=false;}
 if(kind==3){AB_STATE[1]=AB_ENABLED[1]?2:1;}
 if(kind==4){AB_HOMED[1]=true;AB_ACTUALPOSITION[1]=0;AB_STATE[1]=2;}
 if(kind==5){
  auto p=static_cast<double>(AB_POSITION[1]);auto v=static_cast<double>(AB_VELOCITY[1]);
  if(p < -2 || p > 2 || v <= 0 || v > 1){AB_ERROR[1]=2007;AB_DONE[1]=false;return;}
  positions.push_back(p);move_times.push_back(now_ns);remaining=50;target=p;AB_MOTIONID[1]=id;AB_STATE[1]=4;AB_ACTUALVELOCITY[1]=v;AB_BUSY[1]=true;AB_DONE[1]=false;
 }
 if(kind==7){remaining=0;AB_ACTUALVELOCITY[1]=0;AB_STATE[1]=AB_ENABLED[1]?2:1;}
}
static void check(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
int main(int argc,char** argv){try{
 check(argc==2,"scenario required");std::string mode=argv[1];Program_MAIN p;AB_STATE[1]=1;long long scan_count=0;
 auto scan=[&]{now_ns=scan_count++*10000000LL;__CURRENT_TIME_NS=now_ns;p.run();};
 for(int i=0;i<5;++i)scan();
 check(commands==std::vector<unsigned>{2},"cold start enabled or moved axis");
 if(mode=="cold"){for(int i=0;i<500;++i)scan();check(positions.empty(),"cold start moved");}
 else{
  p.POWERENABLE=true;for(int i=0;i<5;++i)scan();
  if(mode!="unhomed"){p.HOMEEXECUTE=true;for(int i=0;i<5;++i)scan();p.HOMEEXECUTE=false;for(int i=0;i<5;++i)scan();}
  if(mode=="offset")AB_ACTUALPOSITION[1]=0.2;
  if(mode=="limits")AB_ACTUALPOSITION[1]=1;
  if(mode=="overspeed")p.SPEED=1.1;
  p.CYCLEEXECUTE=true;for(int i=0;i<8;++i)scan();
  if(mode=="unhomed")check(positions.empty()&&p.STEP==0,"unhomed cycle accepted");
  else if(mode=="limits"||mode=="overspeed")check(positions.empty()&&p.STEP==900&&p.ERRORID==2007,"native command rejection was not surfaced without a 71xx wrapper");
  else if(mode=="disconnect"){
   disconnected=true;for(int i=0;i<8;++i)scan();check(p.STEP==900&&p.ERRORID==2004&&!p.DONE,"lost feedback falsely completed or hid initial ErrorID");
  }else{
   if(mode=="cancel")p.STOPEXECUTE=true;
   if(mode=="pulse")p.CYCLEEXECUTE=false;
   for(int i=0;i<1000;++i)scan();
   if(mode=="cancel")check(p.CANCELLED&&!p.DONE&&positions.size()==1,"cancel executed reverse or claimed Done");
   else{
    auto base=mode=="offset"?0.2:0;
    check(positions.size()==2&&p.ERRORID==0,"standard Done sequence did not finish exactly once");
    check(std::abs(positions[0]-(base+1.5))<1e-9&&std::abs(positions[1]-(base+.5))<1e-9,"wrong relative turn targets");
    check(move_times[1]-forward_done_ns>=2000000000LL,"dwell did not wait two seconds after Done");
    check(mode=="pulse"?p.STEP==0:static_cast<bool>(p.DONE),"held or pulsed cycle result incorrect");
   }
  }
 }
 std::cout<<"PASS "<<mode<<'\n';
 }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}}

// SPDX-License-Identifier: MIT
// Memory-only tests against the actual ST compiler output. No hardware access.
#include "generated.hpp"
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
using namespace strucpp;
static std::uint64_t seen=0;
static long long now_ns=0;
static std::vector<unsigned> commands;
static std::vector<double> positions;
static std::vector<long long> move_times;
extern "C" void workbench_axis_after_scan(bool ok) {
 if(!ok) throw std::runtime_error("scan failure");
 auto id=static_cast<std::uint64_t>(AB_REQUEST[1]); if(id==seen)return;seen=id;
 auto kind=static_cast<unsigned>(AB_KIND[1]);commands.push_back(kind);
 AB_FEEDBACKREQUEST[1]=id;AB_BUSY[1]=false;AB_DONE[1]=true;AB_ERROR[1]=0;AB_ABORTED[1]=false;
 if(kind==1){AB_ENABLED[1]=true;AB_STATE[1]=2;}
 if(kind==2){AB_ENABLED[1]=false;AB_STATE[1]=1;}
 if(kind==4){AB_HOMED[1]=true;AB_ACTUALPOSITION[1]=0;AB_STATE[1]=2;}
 if(kind==5){positions.push_back(AB_POSITION[1]);move_times.push_back(now_ns);AB_ACTUALPOSITION[1]=AB_POSITION[1];AB_ACTUALVELOCITY[1]=0;AB_STATE[1]=2;AB_MOTIONID[1]=id;}
 if(kind==7){AB_ACTUALVELOCITY[1]=0;AB_STATE[1]=AB_ENABLED[1]?2:1;}
}
static void check(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
int main(int argc,char** argv){try{
 check(argc==2,"scenario required");std::string mode=argv[1];Program_MAIN p;AB_STATE[1]=1;long long tick=0;
 auto scan=[&]{now_ns=tick++*10000000LL;__CURRENT_TIME_NS=now_ns;p.run();};
 for(int n=0;n<5;++n)scan();
 check(commands==std::vector<unsigned>{2},"cold start must only request disabled state");
 if(mode=="cold"){for(int n=0;n<500;++n)scan();check(positions.empty()&&!p.CYCLEACTIVE&&!AB_ENABLED[1],"cold start moved or enabled");}
 else{
 p.POWERENABLE=true;for(int n=0;n<5;++n)scan();
 check(AB_ENABLED[1],"manual enable failed");
 if(mode!="unhomed"){p.HOMEEXECUTE=true;for(int n=0;n<5;++n)scan();p.HOMEEXECUTE=false;for(int n=0;n<5;++n)scan();}
 if(mode=="offset")AB_ACTUALPOSITION[1]=0.2;
 if(mode=="limits")AB_ACTUALPOSITION[1]=1;
 if(mode=="feedback-loss")AB_STATE[1]=0;
 p.CYCLEEXECUTE=true;scan();
 if(mode=="unhomed"||mode=="limits"||mode=="feedback-loss"){
  for(int n=0;n<20;++n)scan();check(positions.empty()&&p.FAILED&&!p.CYCLEACTIVE,"invalid start moved axis");
 }else{
  if(mode=="cancel"){for(int n=0;n<100&&p.STEP!=20;++n)scan();p.STOPEXECUTE=true;}
  for(int n=0;n<1000&&p.CYCLEACTIVE;++n)scan();
  if(mode=="cancel")check(!p.DONE&&p.CANCELLED&&positions.size()<=1,"cancel still dispatched reverse or claimed success");
  else{
   const double base=mode=="offset"?0.2:0;
   check(p.DONE&&!p.FAILED&&positions.size()==2,"sequence did not complete");
   check(std::abs(positions[0]-(base+1.5))<1e-9&&std::abs(positions[1]-(base+.5))<1e-9,"incorrect turn targets");
   check(move_times[1]-move_times[0]>=2000000000LL,"dwell shorter than 2s");
   auto count=positions.size();for(int n=0;n<500;++n)scan();check(positions.size()==count,"held Execute retriggered");
   check(AB_ENABLED[1],"cycle unexpectedly removed explicit PowerEnable");
   p.POWERENABLE=false;for(int n=0;n<5;++n)scan();check(!AB_ENABLED[1],"manual disable failed");
  }
 }
 }
 std::cout<<"PASS "<<mode<<'\n';
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}

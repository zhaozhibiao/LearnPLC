// SPDX-License-Identifier: MIT
// Linux-only actual ST + native transport + worker test, driven by verify_e2e.py.
#include "generated.hpp"
#include "communication_bridge.hpp"
#include <chrono>
#include <cstdio>
#include <thread>
using namespace strucpp;
strucpp::Configuration_CONFIG0 g_config;
int main(int argc,char** argv) {
 if(argc!=2 || !workbench_comm_start(argv[1]))return 2;
 WB_MB_REQUEST mb; mb.CHANNEL=1;mb.EXECUTE=true;mb.FUNCTIONCODE=3;mb.UNITID=1;mb.ADDRESS=0;mb.QUANTITY=2;
 bool completed=false;
 for(unsigned i=0;i<200;++i){
  mb();workbench_comm_after_scan(true);
  if(mb.ERROR){std::fprintf(stderr,"MB error %u\n",unsigned(mb.ERRORID.get()));workbench_comm_stop();return 3;}
  if(mb.DONE){completed=true;break;}
  std::this_thread::sleep_for(std::chrono::milliseconds(10));
 }
 if(!completed || mb.RECEIVEDLENGTH.get()!=4 || mb.RECEIVEDDATA[0].get()!=0x12 || mb.RECEIVEDDATA[1].get()!=0x34 || mb.RECEIVEDDATA[2].get()!=0x56 || mb.RECEIVEDDATA[3].get()!=0x78 || !mb.ACKNOWLEDGED){workbench_comm_stop();return 4;}
 for(unsigned i=0;i<30;++i){mb();workbench_comm_after_scan(true);std::this_thread::sleep_for(std::chrono::milliseconds(10));}
 if(mb.REQUESTID.get()!=1 || !mb.DONE){workbench_comm_stop();return 5;}
 // Starve scans while keeping the bridge alive: Linux worker must close TCP itself.
 std::this_thread::sleep_for(std::chrono::milliseconds(500));
 workbench_comm_stop();
 std::puts("PASS actual ST Modbus read, binary response, held Execute and scan watchdog integration");return 0;
}

// SPDX-License-Identifier: MIT
// Executes compiler-generated ST plus the unmodified production scan shim.
// Native transport is a deliberate test double at the asynchronous mailbox boundary.
#include "generated.hpp"
#include "communication_bridge.hpp"
#include <deque>
#include <cstdio>
#include <cstdlib>
#include <limits>
using namespace strucpp;
namespace workbench_comm{
std::deque<FeedbackPacket> pending;
CommandPacket captured{};bool accept=true;unsigned submissions=0;
bool submit(const CommandPacket& p)noexcept{captured=p;++submissions;return accept;}
bool poll(FeedbackPacket& p)noexcept{if(pending.empty())return false;p=pending.front();pending.pop_front();return true;}
}
int checks=0;
void check(bool ok,const char* name){++checks;if(!ok){std::fprintf(stderr,"FAIL %s\n",name);std::exit(1);}}
void reset(){
 CB_SCANCOUNTER=0;
 for(int i=1;i<=16;i++){CB_REQUEST[i]=0;CB_ACTION[i]=0;CB_FUNCTION[i]=0;CB_UNIT[i]=0;CB_ADDRESS[i]=0;CB_QUANTITY[i]=0;CB_READADDRESS[i]=0;CB_READQUANTITY[i]=0;CB_LENGTH[i]=0;CB_FEEDBACK[i]=0;CB_BUSY[i]=false;CB_DONE[i]=false;CB_ERROR[i]=0;CB_UNKNOWN[i]=false;CB_ACKNOWLEDGED[i]=false;CB_RECEIVEDLENGTH[i]=0;}
 for(int i=0;i<8192;i++){CB_DATA[i]=0;CB_RECEIVEDDATA[i]=0;}
 workbench_comm::pending.clear();workbench_comm::accept=true;workbench_comm::submissions=0;
}
void complete(unsigned channel,std::uint64_t request){CB_FEEDBACK[channel]=request;CB_BUSY[channel]=false;CB_DONE[channel]=true;CB_ERROR[channel]=0;}
int main(){
 reset();WB_TCP_SEND send;send.CHANNEL=1;send.EXECUTE=true;send.DATALENGTH=3;send.DATA[0]=0;send.DATA[1]=127;send.DATA[2]=255;send();check(send.BUSY&&!send.ERROR&&send.REQUESTID.get()==1,"rising edge accepted");check(CB_LENGTH[1].get()==3&&CB_DATA[0].get()==0&&CB_DATA[2].get()==255,"binary payload copied");send();check(CB_REQUEST[1].get()==1,"held execute does not duplicate");send.EXECUTE=false;send();check(CB_REQUEST[1].get()==1,"falling execute no command");complete(1,1);CB_ACKNOWLEDGED[1]=true;CB_RECEIVEDLENGTH[1]=2;CB_RECEIVEDDATA[0]=0;CB_RECEIVEDDATA[1]=255;send();check(send.DONE&&send.ACKNOWLEDGED&&send.RECEIVEDLENGTH.get()==2&&send.RECEIVEDDATA[1].get()==255,"matching success copied");send.EXECUTE=true;send();check(send.BUSY&&!send.DONE&&!send.ACKNOWLEDGED&&send.RECEIVEDLENGTH.get()==0&&send.REQUESTID.get()==2,"new request clears prior result");send();check(send.BUSY&&!send.DONE,"old feedback ignored");
 reset();WB_TCP_SEND first;WB_TCP_SEND second;first.CHANNEL=16;first.EXECUTE=true;first.DATALENGTH=512;first.DATA[511]=99;first();check(CB_DATA[8191].get()==99,"last channel last byte in bounds");second.CHANNEL=16;second.EXECUTE=true;second();check(second.ERROR&&second.ERRORID.get()==6006&&CB_REQUEST[16].get()==1,"two instances contend without overwriting");
 reset();WB_TCP_SEND busy;busy.CHANNEL=1;busy.EXECUTE=true;busy();CB_FEEDBACK[1]=1;CB_BUSY[1]=true;busy.EXECUTE=false;busy();busy.EXECUTE=true;busy();check(busy.ERROR&&busy.ERRORID.get()==6006&&!busy.BUSY,"busy rejection survives old matching feedback");busy();check(busy.ERROR&&busy.ERRORID.get()==6006,"local error survives held execute");
 reset();WB_TCP_SEND invalid;invalid.CHANNEL=0;invalid.EXECUTE=true;invalid();check(invalid.ERROR&&invalid.ERRORID.get()==6001,"zero channel invalid");invalid.CHANNEL=17;invalid();check(invalid.ERROR,"upper channel invalid");invalid.CHANNEL=65535;invalid();check(invalid.ERROR,"maximum UINT channel invalid");invalid.CHANNEL=1;invalid.EXECUTE=false;invalid();invalid.EXECUTE=true;invalid.DATALENGTH=513;invalid();check(invalid.ERROR&&invalid.ERRORID.get()==6007&&CB_REQUEST[1].get()==0,"oversize input rejected");invalid.EXECUTE=false;invalid();invalid.DATALENGTH=512;invalid.EXECUTE=true;invalid();check(invalid.BUSY&&!invalid.ERROR,"new valid edge recovers");complete(1,1);invalid.EXECUTE=false;invalid();invalid.CHANNEL=0;invalid();check(invalid.ERROR&&invalid.ERRORID.get()==6001,"invalid channel not hidden by old success");
 reset();CB_COMMAND action;action.CHANNEL=1;action.EXECUTE=true;action.ACTION=0;action();check(action.ERROR&&action.ERRORID.get()==6007,"zero action invalid");action.EXECUTE=false;action();action.ACTION=6;action.EXECUTE=true;action();check(action.ERROR&&action.ERRORID.get()==6007,"upper action invalid");
 reset();CB_REQUEST[1]=std::numeric_limits<std::uint64_t>::max();CB_FEEDBACK[1]=CB_REQUEST[1];WB_TCP_SEND exhausted;exhausted.CHANNEL=1;exhausted.EXECUTE=true;exhausted();check(exhausted.ERROR&&exhausted.ERRORID.get()==6009&&CB_REQUEST[1].get()==std::numeric_limits<std::uint64_t>::max(),"request counter never wraps");
 reset();WB_TCP_RECEIVE receiver;receiver.CHANNEL=1;receiver.EXECUTE=true;receiver();CB_FEEDBACK[1]=1;CB_DONE[1]=true;CB_RECEIVEDLENGTH[1]=513;receiver();check(receiver.ERROR&&receiver.ERRORID.get()==6004&&!receiver.DONE&&!receiver.BUSY,"oversize receive feedback rejected");
 reset();WB_TCP_SEND running;WB_TCP_CLOSE close;running.CHANNEL=1;running.EXECUTE=true;running();CB_FEEDBACK[1]=1;CB_BUSY[1]=true;close.CHANNEL=1;close.EXECUTE=true;close();check(close.BUSY&&!close.ERROR&&close.REQUESTID.get()==2&&CB_ACTION[1].get()==5,"close preempts busy transaction");running();check(running.ERROR&&running.ERRORID.get()==6006&&running.UNKNOWN,"preempted operation explicitly unknown");complete(1,2);close();check(close.DONE&&!close.ERROR,"close exact feedback succeeds");
 reset();WB_TCP_SEND pending;pending.CHANNEL=1;pending.EXECUTE=true;pending();WB_TCP_CLOSE pendingClose;pendingClose.CHANNEL=1;pendingClose.EXECUTE=true;pendingClose();check(pendingClose.BUSY&&CB_REQUEST[1].get()==2,"close preempts unacknowledged request");
 reset();WB_MB_REQUEST mb;mb.CHANNEL=2;mb.EXECUTE=true;mb.FUNCTIONCODE=23;mb.UNITID=247;mb.ADDRESS=65500;mb.QUANTITY=10;mb.READADDRESS=10;mb.READQUANTITY=11;mb();check(CB_ACTION[2].get()==1&&CB_FUNCTION[2].get()==23&&CB_UNIT[2].get()==247&&CB_ADDRESS[2].get()==65500&&CB_READQUANTITY[2].get()==11,"Modbus fields serialized without truncation");
 reset();WB_SERIAL_SEND serial;serial.CHANNEL=1;serial.EXECUTE=true;serial();check(CB_ACTION[1].get()==3,"serial send mapped");WB_SERIAL_RECEIVE serialReceive;serialReceive.CHANNEL=2;serialReceive.EXECUTE=true;serialReceive();check(CB_ACTION[2].get()==4,"serial receive mapped");WB_TCP_CONNECT connect;connect.CHANNEL=3;connect.EXECUTE=true;connect();check(CB_ACTION[3].get()==2,"TCP connect mapped");
 reset();CB_REQUEST[1]=5;CB_ACTION[1]=3;CB_DATA[511]=255;workbench_comm_after_scan(true);check(CB_SCANCOUNTER.get()==1&&workbench_comm::captured.scan==1&&workbench_comm::captured.flags==1&&workbench_comm::captured.channels[0].request==5&&workbench_comm::captured.channels[0].data[511]==255,"scan hook snapshot exact");workbench_comm_after_scan(false);check(CB_SCANCOUNTER.get()==1&&workbench_comm::captured.flags==0,"failed scan emits stop flag");
 reset();CB_REQUEST[1]=2;workbench_comm::FeedbackPacket feedback{};feedback.channels[0].request=1;feedback.channels[0].done=1;workbench_comm::pending.push_back(feedback);workbench_comm_after_scan(true);check(CB_FEEDBACK[1].get()==0&&!CB_DONE[1],"native stale request feedback ignored");feedback.channels[0].request=2;feedback.channels[0].done=0;feedback.channels[0].error=6455;workbench_comm::pending.push_back(feedback);workbench_comm_after_scan(true);check(CB_ERROR[1].get()==6455,"all byte-sized Modbus exceptions accepted");feedback.channels[0].error=6456;workbench_comm::pending.push_back(feedback);workbench_comm_after_scan(true);check(CB_ERROR[1].get()==6004&&CB_UNKNOWN[1],"native out-of-domain error rejected");
 reset();CB_REQUEST[1]=1;feedback={};feedback.channels[0].request=1;feedback.channels[0].busy=2;workbench_comm::pending.push_back(feedback);workbench_comm_after_scan(true);check(CB_ERROR[1].get()==6004,"native noncanonical bool rejected");
 reset();CB_REQUEST[1]=1;feedback={};feedback.channels[0].request=1;feedback.channels[0].done=1;feedback.channels[0].busy=1;workbench_comm::pending.push_back(feedback);workbench_comm_after_scan(true);check(CB_ERROR[1].get()==6004,"native impossible busy done rejected");
 reset();CB_REQUEST[1]=1;feedback={};feedback.channels[0].request=1;feedback.channels[0].length=513;workbench_comm::pending.push_back(feedback);workbench_comm_after_scan(true);check(CB_ERROR[1].get()==6004,"native excess length rejected");
 reset();CB_REQUEST[1]=1;workbench_comm::accept=false;workbench_comm_after_scan(true);check(CB_FEEDBACK[1].get()==1&&CB_ERROR[1].get()==6004&&CB_UNKNOWN[1],"native queue failure not success");
 reset();for(int i=0;i<40;i++)workbench_comm::pending.emplace_back();workbench_comm_after_scan(true);check(workbench_comm::pending.size()==8,"native per scan drain bounded to32 packets");
 reset();WB_COMM_CLOSE genericClose;genericClose.CHANNEL=1;genericClose.EXECUTE=true;genericClose();check(CB_ACTION[1].get()==5&&genericClose.BUSY,"generic close available for Modbus and serial recovery");complete(1,1);genericClose();check(genericClose.DONE&&!genericClose.ERROR,"generic close confirms matching completion");
 std::printf("PASS %d actual communication ST and native scan-hook checks\n",checks);
}

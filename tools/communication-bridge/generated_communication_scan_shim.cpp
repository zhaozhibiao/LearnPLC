// SPDX-License-Identifier: MIT
#include "generated.hpp"
#include "communication_bridge.hpp"
extern "C" void workbench_comm_after_scan(bool success) {
 using namespace strucpp;using namespace workbench_comm;
 if(success && static_cast<std::uint64_t>(CB_SCANCOUNTER)!=UINT64_MAX) CB_SCANCOUNTER=static_cast<std::uint64_t>(CB_SCANCOUNTER)+1;
 CommandPacket packet{};packet.scan=CB_SCANCOUNTER;packet.flags=success?1:0;
 for(unsigned i=0;i<max_channels;++i){auto a=i+1; auto& c=packet.channels[i]; c.request=CB_REQUEST[a];c.action=CB_ACTION[a];c.function=CB_FUNCTION[a];c.unit=CB_UNIT[a];c.address=CB_ADDRESS[a];c.quantity=CB_QUANTITY[a];c.read_address=CB_READADDRESS[a];c.read_quantity=CB_READQUANTITY[a];c.length=CB_LENGTH[a];for(unsigned j=0;j<max_data;++j)c.data[j]=CB_DATA[i*max_data+j];}
 FeedbackPacket feedback{}; bool invalid=false;
 for(unsigned n=0;n<32 && poll(feedback);++n)for(unsigned i=0;i<max_channels;++i){auto a=i+1;const auto& f=feedback.channels[i];
  if(f.busy>1 || f.done>1 || f.unknown>1 || f.acknowledged>1 || f.length>max_data || f.error>6455 || (f.error && f.error<6001) || (f.done && (f.busy || f.error || f.unknown))){invalid=true;continue;}
  if(f.request==packet.channels[i].request){CB_FEEDBACK[a]=f.request;CB_BUSY[a]=f.busy!=0;CB_DONE[a]=f.done!=0;CB_ERROR[a]=static_cast<std::uint32_t>(f.error);CB_UNKNOWN[a]=f.unknown!=0;CB_ACKNOWLEDGED[a]=f.acknowledged!=0;CB_RECEIVEDLENGTH[a]=f.length;for(unsigned j=0;j<max_data;++j)CB_RECEIVEDDATA[i*max_data+j]=f.data[j];}
 }
 if(invalid || !submit(packet))for(unsigned a=1;a<=max_channels;++a){CB_FEEDBACK[a]=CB_REQUEST[a];CB_BUSY[a]=false;CB_DONE[a]=false;CB_ERROR[a]=6004;CB_UNKNOWN[a]=CB_REQUEST[a]!=0;}
}

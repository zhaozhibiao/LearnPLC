#include "generated.hpp"
#include "axis_bridge.hpp"
#include <cmath>
extern "C" void workbench_axis_after_scan(bool success) {
 using namespace strucpp;using namespace workbench_axis;
 if(success && static_cast<std::uint64_t>(AB_SCANCOUNTER)!=UINT64_MAX) AB_SCANCOUNTER=static_cast<std::uint64_t>(AB_SCANCOUNTER)+1;
 CommandPacket packet{};packet.scan=AB_SCANCOUNTER;packet.flags=success?1:0;
 for(unsigned i=0;i<max_axes;++i){auto a=i+1;packet.axes[i]={AB_REQUEST[a],AB_KIND[a],AB_POSITION[a],AB_VELOCITY[a],AB_STOPHELD[a]?1U:0U};}
 FeedbackPacket feedback{};
 bool invalid=false;
 for(unsigned n=0;n<32 && poll(feedback);++n){
  for(unsigned i=0;i<max_axes;++i){auto a=i+1;const auto& f=feedback.axes[i];
   if(f.busy>1 || f.done>1 || f.aborted>1 || f.enabled>1 || f.homed>1 || f.state>7 || f.error>2009 || (f.error && f.error<2001) || !std::isfinite(f.position) || !std::isfinite(f.velocity) || (f.done && (f.busy || f.error || f.aborted))){invalid=true;continue;}
   // Older workers retained their last sample on infrastructure failure.
   // Keep fresh command/drive errors (2005/2006/2007) readable, but never
   // expose cached state as valid after watchdog/lease/transport/ABI failure.
   const bool unavailable=f.error==2002 || f.error==2003 || f.error==2004 || f.error==2008;
   AB_ENABLED[a]=!unavailable && f.enabled!=0;AB_HOMED[a]=!unavailable && f.homed!=0;AB_STATE[a]=unavailable?0:f.state;AB_MOTIONID[a]=f.motion_id;AB_ACTUALPOSITION[a]=f.position;AB_ACTUALVELOCITY[a]=f.velocity;
   if(f.request==packet.axes[i].request){AB_FEEDBACKREQUEST[a]=f.request;AB_BUSY[a]=f.busy!=0;AB_DONE[a]=f.done!=0;AB_ERROR[a]=static_cast<std::uint32_t>(f.error);AB_ABORTED[a]=f.aborted!=0;}
  }
 }
 if(invalid || !submit(packet))for(unsigned a=1;a<=max_axes;++a){AB_FEEDBACKREQUEST[a]=AB_REQUEST[a];AB_BUSY[a]=false;AB_DONE[a]=false;AB_ERROR[a]=transport_error;AB_STATE[a]=0;}
}

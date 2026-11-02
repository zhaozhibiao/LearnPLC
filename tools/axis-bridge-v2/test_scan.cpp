// Compile with actual STruC++ generated.hpp/generated.cpp and the production scan shim.
#include "generated.hpp"
#include "axis_bridge.hpp"
#include <cassert>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <limits>
using namespace strucpp;
using namespace workbench_axis;
extern CommandPacket axis_fixture_last;
extern bool axis_fixture_submit_ok;
extern unsigned axis_fixture_submissions;
void axis_fixture_publish(const FeedbackPacket&);
static void reset(){
 AB_SCANCOUNTER=0;
 for(unsigned a=1;a<=max_axes;++a){
  AB_REQUEST[a]=0;AB_REQUESTSEQUENCE[a]=0;AB_KIND[a]=0;
  AB_POSITION[a]=0;AB_VELOCITY[a]=0;AB_ACCELERATION[a]=0;AB_DECELERATION[a]=0;AB_JERK[a]=0;
  AB_STOPHELD[a]=false;AB_FEEDBACKREQUEST[a]=0;AB_REJECTEDREQUEST[a]=0;AB_REJECTEDERROR[a]=0;
  AB_BUSY[a]=false;AB_DONE[a]=false;AB_ERROR[a]=0;AB_ABORTED[a]=false;
  AB_ENABLED[a]=false;AB_HOMED[a]=false;AB_STATE[a]=0;AB_MOTIONID[a]=0;AB_ACTUALPOSITION[a]=0;AB_ACTUALVELOCITY[a]=0;
 }
 axis_fixture_submit_ok=true;
}
static void test_zero_image_and_all_slot_copy(){
 reset();workbench_axis_after_scan(true);
 assert(axis_fixture_last.scan==1 && axis_fixture_last.flags==1);
 for(const auto& slot:axis_fixture_last.axes)assert(slot.request==0 && slot.kind==0 && slot.position==0 && slot.velocity==0 && slot.acceleration==0 && slot.deceleration==0 && slot.jerk==0 && slot.flags==0);
 for(unsigned a=1;a<=max_axes;++a){AB_REQUEST[a]=a;AB_REQUESTSEQUENCE[a]=a;AB_KIND[a]=5;AB_POSITION[a]=a+.1;AB_VELOCITY[a]=a+.2;AB_ACCELERATION[a]=a+.3;AB_DECELERATION[a]=a+.4;AB_JERK[a]=a+.5;AB_STOPHELD[a]=true;}
 workbench_axis_after_scan(false);assert(axis_fixture_last.scan==1 && axis_fixture_last.flags==0);
 for(unsigned i=0;i<max_axes;++i){const auto& slot=axis_fixture_last.axes[i];auto a=i+1;assert(slot.request==a && slot.kind==5 && slot.position==a+.1 && slot.velocity==a+.2 && slot.acceleration==a+.3 && slot.deceleration==a+.4 && slot.jerk==a+.5 && slot.flags==1);}
}
static void test_rejected_stop_release_replay(){
 reset();MC_STOP owner{};owner.AXIS=1;owner.EXECUTE=true;owner();
 assert(owner.REQUESTID==1 && AB_REQUESTSEQUENCE[1]==1 && AB_STOPHELD[1]);
 FeedbackPacket feedback{};auto& slot=feedback.axes[0];slot.request=1;slot.done=1;slot.state=2;slot.enabled=1;
 axis_fixture_publish(feedback);workbench_axis_after_scan(true);owner();assert(owner.DONE && owner.BUSY);
 MC_STOP rejected{};rejected.AXIS=1;rejected.EXECUTE=true;rejected();assert(rejected.REQUESTID==2);
 slot.rejected_request=2;slot.rejected_error=2005;
 axis_fixture_publish(feedback);workbench_axis_after_scan(true);
 assert(AB_FEEDBACKREQUEST[1]==1 && AB_REJECTEDREQUEST[1]==2 && AB_REJECTEDERROR[1]==2005 && AB_ERROR[1]==0);
 rejected();assert(rejected.ERROR && rejected.ERRORID==2005 && AB_STOPHELD[1]);
 owner.EXECUTE=false;owner();assert(AB_REQUEST[1]==1 && AB_REQUESTSEQUENCE[1]==2 && !AB_STOPHELD[1]);
 // Outgoing owner replay is 1, while the retained rejection receipt is 2.
 axis_fixture_publish(feedback);workbench_axis_after_scan(true);
 assert(axis_fixture_last.axes[0].request==1 && axis_fixture_last.axes[0].flags==0);
 assert(AB_ERROR[1]==0 && AB_FEEDBACKREQUEST[1]==1 && AB_REJECTEDREQUEST[1]==2 && AB_STATE[1]==2);
 MC_STOP next{};next.AXIS=1;next.EXECUTE=true;next();assert(next.REQUESTID==3 && AB_REQUESTSEQUENCE[1]==3);
}
static void test_future_and_malformed_feedback_fails_closed(){
 for(unsigned variant=0;variant<4;++variant){
  reset();AB_REQUEST[1]=1;AB_REQUESTSEQUENCE[1]=2;
  FeedbackPacket feedback{};feedback.axes[0].request=1;
  if(variant==0)feedback.axes[0].request=3;
  if(variant==1)feedback.axes[0].rejected_request=3;
  if(variant==2)feedback.axes[0].position=std::numeric_limits<double>::quiet_NaN();
  if(variant==3){feedback.axes[0].done=1;feedback.axes[0].busy=1;}
  axis_fixture_publish(feedback);workbench_axis_after_scan(true);
  assert(AB_ERROR[1]==transport_error && AB_STATE[1]==0 && !AB_BUSY[1] && !AB_DONE[1]);
 }
 reset();axis_fixture_submit_ok=false;workbench_axis_after_scan(true);assert(AB_ERROR[64]==transport_error);
}
int main(){
 static_assert(sizeof(CommandPacket)==4112 && sizeof(FeedbackPacket)==6656);
 assert(workbench_axis_v2()==2 && workbench_axis_bootstrap());
 assert(!workbench_axis_start(nullptr));assert(workbench_axis_start("fixture"));assert(!workbench_axis_start("fixture"));workbench_axis_stop();workbench_axis_stop();
 test_zero_image_and_all_slot_copy();test_rejected_stop_release_replay();test_future_and_malformed_feedback_fails_closed();
 std::cout<<"actual generated ST + production v2 shim: all-slot copies, Stop replay/highwater rejection receipts, malformed feedback and lifecycle stub passed\n";
}

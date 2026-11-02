// Compile with the real STruC++ v2 package's generated.cpp and runtime headers.
#include "generated.hpp"
#include <cassert>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <limits>
extern "C" void workbench_axis_after_scan(bool) {}
using namespace strucpp;
static void clear_axis(unsigned axis) {
    AB_REQUEST[axis] = 0; AB_REQUESTSEQUENCE[axis] = 0; AB_FEEDBACKREQUEST[axis] = 0; AB_REJECTEDREQUEST[axis] = 0; AB_REJECTEDERROR[axis] = 0; AB_BUSY[axis] = false;
    AB_DONE[axis] = false; AB_ERROR[axis] = 0; AB_ABORTED[axis] = false;
    AB_STOPHELD[axis] = false; AB_STATE[axis] = 2; AB_MOTIONID[axis] = 1;
    AB_ACCELERATION[axis] = 0.; AB_DECELERATION[axis] = 0.; AB_JERK[axis] = 0.;
}
static MC_MOVEABSOLUTE move(unsigned axis, double a=2., double d=3., double j=4.) {
    MC_MOVEABSOLUTE fb; fb.AXIS=axis; fb.EXECUTE=true; fb.POSITION=5.; fb.VELOCITY=1.;
    fb.ACCELERATION=a; fb.DECELERATION=d; fb.JERK=j; return fb;
}
int main() {
    clear_axis(1);
    auto fb=move(1); fb();
    assert(fb.BUSY && !fb.ERROR && uint64_t(AB_REQUEST[1])==1);
    assert(double(AB_ACCELERATION[1])==2. && double(AB_DECELERATION[1])==3. && double(AB_JERK[1])==4.);
    fb.ACCELERATION=9.; fb.DECELERATION=8.; fb.JERK=7.; fb.POSITION=6.; fb.AXIS=2; fb();
    assert(uint64_t(AB_REQUEST[1])==1 && uint64_t(AB_REQUEST[2])==0);
    assert(double(AB_ACCELERATION[1])==2. && double(AB_DECELERATION[1])==3. && double(AB_JERK[1])==4. && double(AB_POSITION[1])==5.);
    fb.EXECUTE=false; fb(); fb.EXECUTE=true; fb();
    assert(fb.BUSY && uint64_t(AB_REQUEST[1])==1 && uint64_t(AB_REQUEST[2])==0);
    // A new rising edge while Running also cannot retarget or replace the request.
    fb.EXECUTE=false;
    AB_FEEDBACKREQUEST[1]=1; AB_DONE[1]=true; fb();
    assert(fb.DONE && !fb.BUSY); fb(); assert(!fb.DONE);
    fb.AXIS=1; fb.EXECUTE=true; fb();
    assert(uint64_t(AB_REQUEST[1])==2 && double(AB_ACCELERATION[1])==9. && double(AB_JERK[1])==7.);
    for (double bad : {-1., -std::numeric_limits<double>::infinity(), std::numeric_limits<double>::infinity(), std::numeric_limits<double>::quiet_NaN()}) {
        for (int field=0;field<3;field++) {
            clear_axis(3); auto rejected=move(3);
            if(field==0)rejected.ACCELERATION=bad; if(field==1)rejected.DECELERATION=bad; if(field==2)rejected.JERK=bad;
            rejected();
            assert(rejected.ERROR && unsigned(rejected.ERRORID)==2007 && !rejected.BUSY && uint64_t(AB_REQUEST[3])==0);
            assert(double(AB_ACCELERATION[3])==0. && double(AB_DECELERATION[3])==0. && double(AB_JERK[3])==0.);
        }
    }
    clear_axis(4); auto zero=move(4,0.,0.,0.); zero();
    assert(zero.BUSY && !zero.ERROR && uint64_t(AB_REQUEST[4])==1 && double(AB_JERK[4])==0.);
    // Request-identity overflow never publishes a partial image.
    clear_axis(5); AB_REQUEST[5]=std::numeric_limits<uint64_t>::max(); AB_FEEDBACKREQUEST[5]=std::numeric_limits<uint64_t>::max();
    auto overflow=move(5); overflow(); assert(overflow.ERROR && unsigned(overflow.ERRORID)==2009 && double(AB_ACCELERATION[5])==0.);
    // All 64 engineering slots, including the highest index, share the same semantics.
    for(unsigned axis=1;axis<=64;axis++) { clear_axis(axis); auto slot=move(axis,axis,axis+1.,axis+2.); slot(); assert(slot.BUSY && double(AB_JERK[axis])==axis+2.); }
    clear_axis(6);
    MC_MOVEVELOCITY jog; jog.AXIS=6; jog.EXECUTE=true; jog.VELOCITY=-2.;
    jog.ACCELERATION=2.; jog.DECELERATION=3.; jog.JERK=4.; jog();
    assert(jog.BUSY && uint64_t(AB_REQUEST[6])==1 && double(AB_VELOCITY[6])==-2.);
    jog.ACCELERATION=9.; jog.DECELERATION=8.; jog.JERK=7.; jog.VELOCITY=3.; jog();
    jog.EXECUTE=false; jog(); jog.EXECUTE=true; jog();
    assert(jog.BUSY && uint64_t(AB_REQUEST[6])==1 && double(AB_ACCELERATION[6])==2. && double(AB_DECELERATION[6])==3. && double(AB_JERK[6])==4. && double(AB_VELOCITY[6])==-2.);
    MC_STOP stopping; stopping.AXIS=6; stopping.EXECUTE=true; stopping.DECELERATION=5.; stopping.JERK=6.; stopping();
    assert(stopping.BUSY && uint64_t(AB_REQUEST[6])==2 && uint64_t(AB_KIND[6])==7 && bool(AB_STOPHELD[6]));
    assert(double(AB_ACCELERATION[6])==0. && double(AB_DECELERATION[6])==5. && double(AB_JERK[6])==6.);
    stopping.DECELERATION=8.; stopping.JERK=9.; stopping();
    stopping.EXECUTE=false; stopping(); stopping.EXECUTE=true; stopping();
    assert(stopping.BUSY && uint64_t(AB_REQUEST[6])==2 && double(AB_DECELERATION[6])==5. && double(AB_JERK[6])==6.);
    jog(); assert(jog.BUSY && !jog.COMMANDABORTED); // Local publication is not native acceptance.
    AB_FEEDBACKREQUEST[6]=2; AB_BUSY[6]=true; jog(); assert(jog.COMMANDABORTED);
    for (double bad : {-1., -std::numeric_limits<double>::infinity(), std::numeric_limits<double>::infinity(), std::numeric_limits<double>::quiet_NaN()}) {
        for (int field=0;field<3;field++) {
            clear_axis(7); MC_MOVEVELOCITY rejected; rejected.AXIS=7; rejected.EXECUTE=true; rejected.VELOCITY=1.;
            if(field==0)rejected.ACCELERATION=bad; if(field==1)rejected.DECELERATION=bad; if(field==2)rejected.JERK=bad;
            rejected(); assert(rejected.ERROR && unsigned(rejected.ERRORID)==2007 && uint64_t(AB_REQUEST[7])==0);
        }
        for (int field=0;field<2;field++) {
            clear_axis(8); auto running=move(8); running();
            MC_STOP rejected; rejected.AXIS=8; rejected.EXECUTE=true;
            if(field==0)rejected.DECELERATION=bad; else rejected.JERK=bad;
            rejected(); assert(rejected.ERROR && unsigned(rejected.ERRORID)==2007 && uint64_t(AB_REQUEST[8])==1 && !bool(AB_STOPHELD[8]));
            assert(uint64_t(AB_KIND[8])==5 && double(AB_ACCELERATION[8])==2. && double(AB_DECELERATION[8])==3. && double(AB_JERK[8])==4.);
        }
    }
    // An accepted move survives a definitively rejected newer Stop.
    clear_axis(9); auto retained=move(9); retained();
    AB_FEEDBACKREQUEST[9]=1; AB_BUSY[9]=true; retained();
    MC_STOP deniedStop; deniedStop.AXIS=9; deniedStop.EXECUTE=true; deniedStop.DECELERATION=1.; deniedStop.JERK=1.; deniedStop();
    retained(); assert(retained.BUSY && !retained.COMMANDABORTED);
    AB_REJECTEDREQUEST[9]=2; AB_REJECTEDERROR[9]=2007;
    deniedStop(); retained();
    assert(deniedStop.ERROR && unsigned(deniedStop.ERRORID)==2007 && !deniedStop.BUSY && !deniedStop.DONE);
    assert(retained.BUSY && !retained.ERROR && !retained.COMMANDABORTED && !bool(AB_STOPHELD[9]));
    AB_BUSY[9]=false; AB_DONE[9]=true; AB_MOTIONID[9]=55; AB_STATE[9]=2; retained();
    assert(retained.DONE && !retained.BUSY && !retained.ERROR && !retained.COMMANDABORTED);
    // The newer rejected Stop must not cause an accepted-position proof to be skipped.
    clear_axis(10); auto needsProof=move(10); needsProof(); AB_FEEDBACKREQUEST[10]=1; AB_BUSY[10]=true; needsProof();
    MC_STOP deniedAgain; deniedAgain.AXIS=10; deniedAgain.EXECUTE=true; deniedAgain();
    AB_REJECTEDREQUEST[10]=2; AB_REJECTEDERROR[10]=2007; deniedAgain();
    AB_BUSY[10]=false; AB_DONE[10]=true; AB_MOTIONID[10]=0; needsProof();
    assert(needsProof.ERROR && unsigned(needsProof.ERRORID)==2008 && !needsProof.DONE);
    // Accepted supersession aborts only when its receipt arrives.
    clear_axis(11); auto replaced=move(11); replaced(); AB_FEEDBACKREQUEST[11]=1; AB_BUSY[11]=true; replaced();
    MC_STOP acceptedStop; acceptedStop.AXIS=11; acceptedStop.EXECUTE=true; acceptedStop(); replaced();
    assert(replaced.BUSY && !replaced.COMMANDABORTED);
    AB_FEEDBACKREQUEST[11]=2; AB_BUSY[11]=true; acceptedStop(); replaced();
    assert(replaced.COMMANDABORTED && !replaced.BUSY && !replaced.DONE && acceptedStop.BUSY);
    // A locally overwritten request that never reached native cannot manufacture Done.
    clear_axis(12); auto neverAccepted=move(12); neverAccepted();
    MC_STOP rejectedOverwrite; rejectedOverwrite.AXIS=12; rejectedOverwrite.EXECUTE=true; rejectedOverwrite();
    AB_REJECTEDREQUEST[12]=2; AB_REJECTEDERROR[12]=2007; rejectedOverwrite(); neverAccepted();
    assert(neverAccepted.COMMANDABORTED && !neverAccepted.BUSY && !neverAccepted.DONE);
    // A rejected latest request is no longer pending after the retained move finishes.
    MC_MOVEABSOLUTE retry=move(9); retry(); assert(retry.BUSY && uint64_t(AB_REQUEST[9])==3);
    // A rejected newer Stop cannot release an existing accepted held Stop.
    clear_axis(13); MC_STOP held; held.AXIS=13; held.EXECUTE=true; held();
    AB_FEEDBACKREQUEST[13]=1; AB_DONE[13]=true; held(); assert(held.DONE && held.BUSY && bool(AB_STOPHELD[13]));
    MC_STOP refused; refused.AXIS=13; refused.EXECUTE=true; refused();
    AB_REJECTEDREQUEST[13]=2; AB_REJECTEDERROR[13]=2007; refused(); assert(refused.ERROR && bool(AB_STOPHELD[13]));
    refused.EXECUTE=false; refused(); assert(bool(AB_STOPHELD[13]));
    held.EXECUTE=false; held();
    assert(!bool(AB_STOPHELD[13]) && uint64_t(AB_REQUEST[13])==1 && uint64_t(AB_REQUESTSEQUENCE[13])==2);
    MC_MOVEABSOLUTE afterHeld=move(13); afterHeld(); assert(uint64_t(AB_REQUEST[13])==3 && afterHeld.BUSY);
    clear_axis(14); auto faulted=move(14); faulted(); AB_FEEDBACKREQUEST[14]=1; AB_BUSY[14]=true; faulted();
    MC_STOP deniedBeforeFault; deniedBeforeFault.AXIS=14; deniedBeforeFault.EXECUTE=true; deniedBeforeFault();
    AB_REJECTEDREQUEST[14]=2; AB_REJECTEDERROR[14]=2007; deniedBeforeFault();
    AB_BUSY[14]=false; AB_ERROR[14]=2005; faulted();
    assert(faulted.ERROR && unsigned(faulted.ERRORID)==2005 && !faulted.BUSY && !faulted.DONE && !faulted.COMMANDABORTED);
    clear_axis(15); MC_STOP inheritedStop; inheritedStop.AXIS=15; inheritedStop.EXECUTE=true; inheritedStop();
    assert(inheritedStop.BUSY && double(AB_ACCELERATION[15])==0. && double(AB_DECELERATION[15])==0. && double(AB_JERK[15])==0.);
    std::cout << "PASS real generated ST rising-edge/Busy immutability, release/completion, negative/NaN/Inf rejection, zero inheritance image, overflow, all 64 slots, jog and Stop dynamics, accepted/rejected supersession receipts and retained-owner real terminal proof\n";
}

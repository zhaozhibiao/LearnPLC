// SPDX-License-Identifier: MIT
// Deterministic memory-only execution of the actual generated ST. No device IO.
#include "generated.hpp"
#include <cmath>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>
using namespace strucpp;
static std::vector<unsigned> commands;
static std::vector<double> positions;
static std::vector<long long> move_times;
static std::uint64_t seen;
static long long now_ns;
static bool withhold;
static bool fail_home;
extern "C" void workbench_axis_after_scan(bool success) {
    if (!success) throw std::runtime_error("scan failed");
    auto id = static_cast<std::uint64_t>(AB_REQUEST[1]);
    if (id == seen) return;
    seen = id;
    auto kind = static_cast<unsigned>(AB_KIND[1]);
    commands.push_back(kind);
    if (kind == 5) { positions.push_back(AB_POSITION[1]); move_times.push_back(now_ns); }
    if (withhold) return;
    AB_FEEDBACKREQUEST[1] = id;
    AB_BUSY[1] = false; AB_DONE[1] = true; AB_ERROR[1] = 0; AB_ABORTED[1] = false;
    if (kind == 1) { AB_ENABLED[1] = true; AB_STATE[1] = 2; }
    if (kind == 2) { AB_ENABLED[1] = false; AB_STATE[1] = 1; }
    if (kind == 4) {
        if (fail_home) { AB_ERROR[1] = 2005; AB_DONE[1] = false; }
        else { AB_HOMED[1] = true; AB_ACTUALPOSITION[1] = 0; AB_STATE[1] = 2; }
    }
    if (kind == 5) { AB_ACTUALPOSITION[1] = AB_POSITION[1]; AB_ACTUALVELOCITY[1] = 0; AB_STATE[1] = 2; AB_MOTIONID[1] = id; }
    if (kind == 7) { AB_ACTUALVELOCITY[1] = 0; AB_STATE[1] = AB_ENABLED[1] ? 2 : 1; }
}
static void check(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
int main(int argc, char** argv) {
    try {
        check(argc == 2, "scenario required");
        std::string mode = argv[1];
        Program_MAIN p;
        AB_STATE[1] = 1; AB_HOMED[1] = true;
        long long tick = 0;
        auto scan = [&] { now_ns = tick++ * 10000000LL; __CURRENT_TIME_NS = now_ns; p.run(); };
        if (mode == "cold") {
            for (int n=0; n<1000; ++n) scan();
            check(commands.empty() && !p.ACTIVE && !p.PASSED, "cold start issued commands");
        } else {
            p.PARAMETERSCONFIRMED = mode != "unconfirmed";
            p.ESTABLISHTEMPORARYZERO = mode != "offset";
            if (mode == "offset") AB_ACTUALPOSITION[1] = 0.2;
            if (mode == "offline") AB_STATE[1] = 0;
            if (mode == "moving") { AB_STATE[1]=4; AB_ENABLED[1]=true; AB_ACTUALVELOCITY[1]=0.005; }
            if (mode == "nan-position") AB_ACTUALPOSITION[1]=std::numeric_limits<double>::quiet_NaN();
            if (mode == "nan-distance") p.DISTANCEREV=std::numeric_limits<double>::quiet_NaN();
            if (mode == "cancel-before-start") p.CANCELTEST=true;
            if (mode == "home-failure") fail_home=true;
            if (mode == "timeout") withhold=true;
            p.RUNONCE=true;
            scan();
            if (mode == "sequence" || mode == "offset") {
                for (int n=0; n<1000 && p.ACTIVE; ++n) scan();
                check(p.PASSED && !p.FAILED && !p.OUTCOMEUNKNOWN, "sequence did not pass");
                double base = mode == "offset" ? 0.2 : 0;
                check(positions.size()==2 && std::abs(positions[0]-(base+1.5))<1e-9 && std::abs(positions[1]-(base+0.5))<1e-9,
                    "must move forward 1.5 turns then reverse exactly 1 turn");
                check(move_times[1]-move_times[0]>=2000000000LL && move_times[1]-move_times[0]<=2050000000LL, "dwell was not 2 seconds after forward completion");
                check(!AB_ENABLED[1] && p.COMPLETEDTESTS==1, "did not disable once");
                auto count=commands.size();
                for(int n=0;n<500;++n) scan();
                check(commands.size()==count, "held RunOnce retriggered");
                if(mode=="sequence") check(commands==std::vector<unsigned>({1,4,5,5,7,2}), "wrong command order");
            } else if (mode == "cancel-dwell" || mode == "feedback-loss" || mode == "home-failure") {
                for(int n=0;n<100 && p.ACTIVE && p.STATE!=40;++n) scan();
                if(mode=="cancel-dwell") p.CANCELTEST=true;
                if(mode=="feedback-loss") AB_STATE[1]=0;
                for(int n=0;n<500 && p.ACTIVE;++n) scan();
                check(p.FAILED && !p.PASSED && !p.ACTIVE, "fault claimed completion");
                check(positions.size()<=1, "fault/cancel still dispatched reverse");
                check(commands.size()>=3 && commands[commands.size()-2]==7 && commands.back()==2, "fault did not stop then disable");
            } else if(mode=="timeout") {
                for(int n=0;n<31000 && p.ACTIVE;++n) scan();
                check(p.FAILED && p.OUTCOMEUNKNOWN && !p.ACTIVE, "timeout failed to latch unknown");
            } else check(commands.empty() && p.FAILED && !p.ACTIVE, "invalid start accepted");
        }
        std::cout << "PASS " << mode << '\n';
    } catch(const std::exception& e) { std::cerr << "FAIL " << argv[1] << ": " << e.what() << '\n'; return 1; }
}

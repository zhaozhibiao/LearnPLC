// MIT. Tests the real ST compiler output with deterministic fake feedback.
// No socket, EtherCAT library, device access or physical command exists here.
#include "generated.hpp"
#include <cmath>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>
using namespace strucpp;
static std::vector<unsigned> commands;
static std::uint64_t seen;
static bool withhold;
static bool fail_home;
extern "C" void workbench_axis_after_scan(bool success) {
    if (!success) throw std::runtime_error("scan failed");
    auto request = static_cast<std::uint64_t>(AB_REQUEST[1]);
    if (request == seen) return;
    seen = request;
    auto kind = static_cast<unsigned>(AB_KIND[1]);
    commands.push_back(kind);
    if (withhold) return;
    AB_FEEDBACKREQUEST[1] = request;
    AB_BUSY[1] = false; AB_DONE[1] = true;
    AB_ERROR[1] = 0; AB_ABORTED[1] = false;
    if (kind == 1) { AB_ENABLED[1] = true; AB_STATE[1] = 2; }
    if (kind == 2) { AB_ENABLED[1] = false; AB_STATE[1] = 1; }
    if (kind == 4) {
        if (fail_home) { AB_ERROR[1] = 2005; AB_DONE[1] = false; }
        else { AB_HOMED[1] = true; AB_ACTUALPOSITION[1] = 0.0; AB_STATE[1] = 2; }
    }
    if (kind == 5) {
        AB_ACTUALPOSITION[1] = AB_POSITION[1];
        AB_ACTUALVELOCITY[1] = 0.0;
        AB_STATE[1] = 2; AB_MOTIONID[1] = request;
    }
    if (kind == 7) {
        AB_ACTUALVELOCITY[1] = 0.0;
        AB_STATE[1] = AB_ENABLED[1] ? 2 : 1;
    }
}
static void require(bool ok, const char* message) {
    if (!ok) throw std::runtime_error(message);
}
int main(int argc, char** argv) {
    try {
        require(argc == 2, "expected scenario");
        std::string mode = argv[1];
        Program_MAIN p;
        AB_STATE[1] = 1; AB_HOMED[1] = true;
        long long tick = 0;
        auto scan = [&] { __CURRENT_TIME_NS = tick++ * 10000000LL; p.run(); };
        if (mode == "cold") {
            for (int n = 0; n < 2000; ++n) scan();
            require(commands.empty() && !p.ACTIVE && !p.PASSED, "cold start issued command");
        } else {
            p.PARAMETERSCONFIRMED = mode != "unconfirmed";
            if (mode == "unhomed") { AB_HOMED[1] = false; p.ESTABLISHTEMPORARYZERO = false; }
            if (mode == "home-roundtrip" || mode == "home-failure") AB_HOMED[1] = false;
            fail_home = mode == "home-failure";
            if (mode == "offline") AB_STATE[1] = 0;
            if (mode == "distance") p.DISTANCEREV = 0.02;
            auto nan = std::numeric_limits<double>::quiet_NaN();
            if (mode == "nan-distance") p.DISTANCEREV = nan;
            if (mode == "nan-velocity") p.VELOCITYREVPERSECOND = nan;
            if (mode == "nan-tolerance") p.POSITIONTOLERANCEREV = nan;
            if (mode == "moving") { AB_ENABLED[1] = true; AB_STATE[1] = 4; AB_ACTUALVELOCITY[1] = 0.005; }
            if (mode == "cancel-before-start") p.CANCELTEST = true;
            withhold = mode == "timeout";
            p.RUNONCE = true;
            scan();
            if (mode == "roundtrip" || mode == "home-roundtrip") {
                for (int n = 0; n < 1000 && p.ACTIVE; ++n) scan();
                require(p.PASSED && !p.FAILED && !p.OUTCOMEUNKNOWN, "roundtrip not passed");
                require(p.COMPLETEDROUNDTRIPS == 1, "wrong completion count");
                require(commands == std::vector<unsigned>({1,4,5,5,7,2}), "wrong command ordering");
                require(std::abs(static_cast<double>(AB_ACTUALPOSITION[1])) < 1e-12 && !AB_ENABLED[1], "did not return and disable");
                for (int n = 0; n < 1000; ++n) scan();
                require(commands.size() == 6 && p.COMPLETEDROUNDTRIPS == 1, "held start retriggered");
            } else if (mode == "cancel" || mode == "feedback-loss" || mode == "home-failure") {
                scan(); scan();
                if (mode == "cancel") p.CANCELTEST = true;
                else AB_STATE[1] = 0;
                for (int n = 0; n < 500 && p.ACTIVE; ++n) scan();
                require(p.FAILED && !p.PASSED && !p.ACTIVE, "fault did not terminate");
                require(commands.size() >= 3 && commands[commands.size()-2] == 7 && commands.back() == 2, "fault did not stop and disable");
            } else if (mode == "timeout") {
                for (int n = 0; n < 2000 && p.ACTIVE; ++n) scan();
                require(p.FAILED && p.OUTCOMEUNKNOWN && !p.PASSED && !p.ACTIVE, "timeout claimed completion");
                require(commands == std::vector<unsigned>({1,7,2}), "timeout did not stop then disable");
            } else {
                require(commands.empty(), "rejected start issued a command");
                require(p.FAILED && !p.ACTIVE && !p.PASSED, "invalid start accepted");
            }
        }
        std::cout << "PASS " << mode << '\n';
    } catch (const std::exception& e) { std::cerr << "FAIL " << argv[1] << ": " << e.what() << '\n'; return 1; }
}

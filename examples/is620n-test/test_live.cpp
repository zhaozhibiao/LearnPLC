// MIT. Runs the actual MCP-compiled ST through the real axis bridge.
// Requires an operator-commissioned physical worker, never a simulator.
#include "generated.hpp"
#include "axis_bridge.hpp"
#include <chrono>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <thread>
using namespace strucpp;
int main(int argc, char** argv) {
    if (argc!=3 || std::string(argv[2])!="--execute-once") return 2;
    if (!workbench_axis_start(argv[1])) return 3;
    struct Cleanup { ~Cleanup() { workbench_axis_stop(); } } cleanup;
    Program_MAIN p;
    auto next=std::chrono::steady_clock::now();
    const auto beginning=next;
    auto scan=[&] {
        __CURRENT_TIME_NS=std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now()-beginning).count();
        p.run(); next+=std::chrono::milliseconds(10); std::this_thread::sleep_until(next);
    };
    try {
        unsigned stationary=0;
        for (unsigned n=0;n<500 && stationary<5;++n) {
            scan();
            if (AB_STATE[1]==1 && AB_ERROR[1]==0 && std::abs(static_cast<double>(AB_ACTUALVELOCITY[1]))<=.001
                && std::abs(static_cast<double>(AB_ACTUALPOSITION[1]))<=1) ++stationary;
            else stationary=0;
        }
        if (stationary<5 || AB_REQUEST[1]!=0) throw std::runtime_error("No verified idle physical axis, or cold start sent a command");
        p.PARAMETERSCONFIRMED=true; p.RUNONCE=true;
        unsigned previous=9999;
        for (unsigned n=0;n<1600;++n) {
            scan();
            if (static_cast<unsigned>(p.STATE)!=previous) {
                previous=p.STATE;
                std::cout << "ST state=" << previous << " position_rev=" << static_cast<double>(p.ACTUALPOSITIONREV)
                    << " velocity_rev_s=" << static_cast<double>(p.ACTUALVELOCITYREVPERSECOND)
                    << " diagnostic=" << static_cast<unsigned>(p.DIAGNOSTICCODE) << std::endl;
            }
            if (!p.ACTIVE && (p.PASSED || p.FAILED)) break;
        }
        if (!p.PASSED || p.FAILED || p.OUTCOMEUNKNOWN || p.COMPLETEDROUNDTRIPS!=1 || AB_ENABLED[1])
            throw std::runtime_error("Compiled ST roundtrip not proven; diagnostic="+std::to_string(static_cast<unsigned>(p.DIAGNOSTICCODE)));
        auto last=static_cast<std::uint64_t>(AB_REQUEST[1]);
        for (unsigned n=0;n<100;++n) scan();
        if (AB_REQUEST[1]!=last || p.COMPLETEDROUNDTRIPS!=1) throw std::runtime_error("Held RunOnce retriggered");
        std::cout << "ST_PHYSICAL_ROUNDTRIP_PASS count=1 disabled=true" << std::endl;
    } catch(const std::exception& error) {
        std::cerr << "ST_PHYSICAL_TEST_FAILED " << error.what() << std::endl;
        p.CANCELTEST=true;
        for(unsigned n=0;n<500 && p.ACTIVE;++n) scan();
        return 1;
    }
}

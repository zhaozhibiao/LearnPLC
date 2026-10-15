// MIT. Isolated simulator only; invoked by integration_candidate.py.
#include "generated.hpp"
#include <dlfcn.h>
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <cmath>

int main(int argc, char** argv) {
    if (argc != 3) return 2;
    setenv("STWORKBENCH_AXIS_BACKEND", "EcMotionSimulator", 1);
    setenv("STWORKBENCH_AXIS_SOCKET", argv[2], 1);
    void* library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library) { std::cerr << dlerror() << '\n'; return 3; }
    try {
        auto get = reinterpret_cast<strucpp::ConfigurationInstance*(*)()>(dlsym(library, "strucpp_get_config"));
        auto* base = get ? get() : nullptr;
        if (!base || base->get_resource_count() != 1) throw std::runtime_error("axis bootstrap failed");
        auto& p = static_cast<strucpp::Configuration_CONFIG0*>(base)->INSTANCE0;
        auto scan = [&] { base->get_resources()[0].tasks[0].programs[0]->run(); std::this_thread::sleep_for(std::chrono::milliseconds(10)); };
        auto wait = [&](auto predicate, auto& fb, const char* name) {
            auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
            do {
                scan();
                if (fb.ERROR) throw std::runtime_error(std::string(name) + " error " + std::to_string(static_cast<unsigned>(fb.ERRORID)));
                if (predicate()) return;
            } while (std::chrono::steady_clock::now() < deadline);
            throw std::runtime_error(std::string(name) + " timed out");
        };
        // Initial false MC_Power confirms Disable before any movement.
        wait([&] { return !p.POWER.BUSY && !p.POWER.STATUS && p.STATUS.VALID; }, p.POWER, "initial Disable");
        p.EXECUTERESET = true;
        wait([&] { return static_cast<bool>(p.RESET.DONE); }, p.RESET, "Reset");
        p.EXECUTERESET = false; scan();
        p.ENABLEAXIS = true;
        wait([&] { return p.POWER.STATUS && !p.POWER.BUSY; }, p.POWER, "Enable");
        p.EXECUTEHOME = true;
        wait([&] { return static_cast<bool>(p.HOME.DONE); }, p.HOME, "Home");
        if (!p.STATUS.HOMED) throw std::runtime_error("homed feedback missing");
        p.EXECUTEHOME = false; scan();
        p.EXECUTEMOVE = true;
        wait([&] { return static_cast<bool>(p.MOVEAXIS.DONE); }, p.MOVEAXIS, "MoveAbsolute");
        if (!p.POSITION.VALID || std::abs(static_cast<double>(p.POSITION.POSITION) - 1.) > .001) throw std::runtime_error("actual position differs");
        p.EXECUTEMOVE = false; scan();
        p.EXECUTEJOG = true;
        wait([&] { return p.JOG.BUSY && p.JOG.INVELOCITY; }, p.JOG, "MoveVelocity");
        if (!p.VELOCITY.VALID || std::abs(static_cast<double>(p.VELOCITY.VELOCITY) - 1.) > .01) throw std::runtime_error("actual velocity differs");
        p.EXECUTESTOP = true;
        wait([&] { return static_cast<bool>(p.STOP.DONE); }, p.STOP, "Stop");
        if (!p.JOG.COMMANDABORTED) throw std::runtime_error("preempted Jog did not abort");
        p.EXECUTEJOG = false; p.EXECUTESTOP = false; scan();
        p.ENABLEAXIS = false;
        wait([&] { return !p.POWER.STATUS && !p.POWER.BUSY; }, p.POWER, "Disable");
        std::this_thread::sleep_for(std::chrono::milliseconds(400));
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
        while (!p.STATUS.ERROR && std::chrono::steady_clock::now() < deadline) scan();
        if (!p.STATUS.ERROR || static_cast<unsigned>(p.STATUS.ERRORID) != 2002) throw std::runtime_error("scan watchdog missing");
        dlclose(library);
        std::cout << "PASS actual PLC SO: Reset/Power/Home/MoveAbsolute/MoveVelocity/Stop/three reads/scan watchdog/lifecycle\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; dlclose(library); return 4;
    }
}

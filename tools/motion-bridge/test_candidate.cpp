#include "generated.hpp"
#include <dlfcn.h>
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <thread>

int main(int argc, char** argv) {
    if (argc != 3) return 2;
    setenv("STWORKBENCH_MOTION_BACKEND", "VirtualContinuous", 1);
    setenv("STWORKBENCH_MOTION_SOCKET", argv[2], 1);
    void* library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library) { std::cerr << dlerror() << '\n'; return 3; }
    auto config = reinterpret_cast<strucpp::ConfigurationInstance*(*)()>(dlsym(library, "strucpp_get_config"));
    auto* base = config ? config() : nullptr;
    if (!base || base->get_resource_count() != 1) return 4;
    auto* fixture = static_cast<strucpp::Configuration_CONFIG0*>(base);
    auto start = std::chrono::steady_clock::now(); bool busy = false;
    while (std::chrono::steady_clock::now() - start < std::chrono::seconds(30)) {
        base->get_resources()[0].tasks[0].programs[0]->run();
        busy = busy || static_cast<bool>(fixture->INSTANCE0.RUNPATH.BUSY);
        if (fixture->INSTANCE0.RUNPATH.ERROR) { std::cerr << "path error " << static_cast<unsigned>(fixture->INSTANCE0.RUNPATH.ERRORID) << '\n'; return 5; }
        if (fixture->INSTANCE0.RUNPATH.DONE) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    if (!busy || !fixture->INSTANCE0.RUNPATH.DONE || static_cast<double>(fixture->INSTANCE0.RUNPATH.PROGRESS) != 1.) return 6;
    if (static_cast<std::uint64_t>(fixture->INSTANCE0.RUNPATH.REQUESTID) != 1) return 7;
    auto read = reinterpret_cast<std::uint16_t(*)(std::uint8_t,std::uint16_t,std::uint8_t*)>(dlsym(library, "strucpp_debug_read"));
    std::uint8_t hash[127]{};
    // The fixture's authoritative debug-map fixes RUNPATH.CONTENTHASH at (0,4).
    if (!read || read(0,4,hash) != 127 || hash[0] != 64 || std::string(reinterpret_cast<char*>(hash + 1),64) != fixture->INSTANCE0.RUNPATH.CONTENTHASH.get().c_str()) return 8;
    dlclose(library);
    std::cout << "PASS actual candidate SO / pinned Runtime entry lifecycle / compiler task scan hook / sealed trajectory worker\n";
}

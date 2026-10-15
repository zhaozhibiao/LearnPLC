// Include only in a motion candidate's runtime_v4_entry.cpp, after its headers.
// Call workbench_motion_bootstrap() in strucpp_get_config(), outside scan execution.
// Explicit capability + private socket configuration is required. The deployment
// coordinator separately verifies sidecar PID/uid/manifest identity before activation.
#include "motion_bridge.hpp"
#include <cstdlib>
#include <cstring>

namespace {
class WorkbenchMotionLifetime {
    bool started_ = false;
public:
    WorkbenchMotionLifetime() {
        const char* backend = std::getenv("STWORKBENCH_MOTION_BACKEND");
        const char* endpoint = std::getenv("STWORKBENCH_MOTION_SOCKET");
        if (backend && std::strcmp(backend, "VirtualContinuous") == 0 &&
            endpoint && endpoint[0] == '/' && std::strlen(endpoint) < 108) {
            started_ = workbench_motion_start(endpoint);
        }
    }
    ~WorkbenchMotionLifetime() {
        if (started_) workbench_motion_stop(); // stops worker before candidate dlclose
    }
    bool available() const { return started_; }
};
bool workbench_motion_bootstrap() {
    // First construction takes place during runtime loading/get_config, never scan.
    // Function-local lifetime destruction precedes bridge TU static thread teardown.
    static WorkbenchMotionLifetime lifetime;
    return lifetime.available();
}
}

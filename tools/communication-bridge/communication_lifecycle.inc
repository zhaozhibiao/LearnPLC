// SPDX-License-Identifier: MIT
#include "communication_bridge.hpp"
#include <cstdlib>
namespace {
struct WorkbenchCommunicationLifetime {
 bool started=false;
 WorkbenchCommunicationLifetime(){started=workbench_comm_start(std::getenv("STWORKBENCH_COMM_SOCKET"));}
 ~WorkbenchCommunicationLifetime(){if(started)workbench_comm_stop();}
};
}
static bool workbench_comm_bootstrap(){static WorkbenchCommunicationLifetime lifetime;return lifetime.started;}

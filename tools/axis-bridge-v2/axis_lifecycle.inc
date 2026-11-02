// Include after headers in the axis candidate runtime entry; bootstrap in get_config.
#include "axis_bridge.hpp"
#include <cstdlib>
#include <cstring>
namespace {
class WorkbenchAxisLifetime {
 bool started_=false;
public:
 WorkbenchAxisLifetime(){
  const char* backend=std::getenv("STWORKBENCH_AXIS_BACKEND");
  const char* socket=std::getenv("STWORKBENCH_AXIS_V2_SOCKET");
  if(backend && (std::strcmp(backend,"EcMotion")==0 || std::strcmp(backend,"EcMotionSimulator")==0))started_=workbench_axis_start(socket);
 }
 ~WorkbenchAxisLifetime(){if(started_)workbench_axis_stop();}
 bool available()const{return started_;}
};
}
extern "C" bool workbench_axis_bootstrap(){static WorkbenchAxisLifetime lifetime;return lifetime.available();}


#include "axis_bridge.hpp"
bool axis_fixture_throw=false;
workbench_axis::CommandPacket axis_fixture_last{};
bool axis_fixture_submit_ok=true;
unsigned axis_fixture_submissions=0;
namespace {
workbench_axis::Mailbox<workbench_axis::FeedbackPacket,64> feedbacks;
bool started=false;
}
void axis_fixture_publish(const workbench_axis::FeedbackPacket& feedback){
 if(!feedbacks.push(feedback))throw 42;
}
namespace workbench_axis {
bool submit(const CommandPacket& packet) noexcept {axis_fixture_last=packet;++axis_fixture_submissions;return axis_fixture_submit_ok;}
bool poll(FeedbackPacket& packet) noexcept {return feedbacks.pop(packet);}
bool start(const char* endpoint){if(!endpoint || started)return false;started=true;return true;}
void stop(){started=false;}
}
extern "C" bool workbench_axis_start(const char* endpoint){return workbench_axis::start(endpoint);}
extern "C" void workbench_axis_stop(){workbench_axis::stop();}
extern "C" bool workbench_axis_bootstrap(){return true;}
extern "C" std::uint32_t workbench_axis_v2(){return 2;}

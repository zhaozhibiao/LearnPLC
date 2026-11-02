#include "axis_bridge.hpp"
bool axis_fixture_throw=false;
workbench_axis::CommandPacket axis_fixture_last{};
workbench_axis::FeedbackPacket axis_fixture_feedback{};
bool axis_fixture_feedback_ready=false;
namespace workbench_axis {
bool submit(const CommandPacket& c) noexcept { axis_fixture_last=c;return true; }
bool poll(FeedbackPacket& feedback) noexcept {
 if(!axis_fixture_feedback_ready)return false;
 feedback=axis_fixture_feedback;axis_fixture_feedback_ready=false;return true;
}
}

#include "axis_bridge.hpp"
bool axis_fixture_throw=false;
workbench_axis::CommandPacket axis_fixture_last{};
namespace workbench_axis {
bool submit(const CommandPacket& c) noexcept { axis_fixture_last=c;return true; }
bool poll(FeedbackPacket&) noexcept { return false; }
}

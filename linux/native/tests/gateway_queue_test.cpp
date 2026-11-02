// SPDX-License-Identifier: MIT
#include "../src/gateway_queue.hpp"
#include <cassert>
int main() {
  using namespace stworkbench;
  using namespace stworkbench::gateway;
  std::vector<Pending> q{
      {0, {{"action", "acquire"}}}, {1, {{"action", "execute"}}},
      {2, {{"action", "stop"}}},    {3, {{"action", "status"}}},
      {4, {{"action", "release"}}}, {5, {{"action", "execute"}}}};
  StopBarrier b;
  auto p = b.pop(q);
  assert(p.sequence == 2 && !b.invalidated(p));
  p = b.pop(q);
  assert(p.sequence == 4 && !b.invalidated(p));
  p = b.pop(q);
  assert(p.sequence == 0 && b.invalidated(p));
  p = b.pop(q);
  assert(p.sequence == 1 && b.invalidated(p));
  p = b.pop(q);
  assert(p.sequence == 3 && !b.invalidated(p));
  p = b.pop(q);
  assert(p.sequence == 5 && !b.invalidated(p));
}

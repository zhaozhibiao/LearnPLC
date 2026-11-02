// SPDX-License-Identifier: MIT
#pragma once
#include "stworkbench/common.hpp"
#include <algorithm>
#include <optional>
namespace stworkbench::gateway {
struct Pending {
  uint64_t sequence;
  Json request;
};
class StopBarrier {
  std::optional<uint64_t> barrier;

public:
  static int priority(const Pending &p) {
    if (!p.request.is_object())
      return -1;
    auto a = p.request.value("action", Json());
    return a == "stop" || a == "release" ? 0 : 1;
  }
  static Pending pop(std::vector<Pending> &queue) {
    auto selected = std::min_element(
        queue.begin(), queue.end(), [](const Pending &a, const Pending &b) {
          int x = priority(a), y = priority(b);
          return x == y ? a.sequence < b.sequence : x < y;
        });
    auto result = *selected;
    queue.erase(selected);
    return result;
  }
  bool invalidated(const Pending &p) {
    auto action =
        p.request.is_object() ? p.request.value("action", Json()) : Json();
    if (action == "stop" || action == "release")
      barrier = barrier ? std::max(*barrier, p.sequence) : p.sequence;
    return barrier && p.sequence < *barrier &&
           (action == "acquire" || action == "execute");
  }
};
} // namespace stworkbench::gateway

// SPDX-License-Identifier: MIT
#include "../src/communication_protocol.hpp"
#include <cassert>
int main() {
  using namespace stcommunication;
  Command c{};
  c.function = 3;
  c.quantity = 2;
  c.address = 100;
  assert(encode(c) == Bytes({3, 0, 100, 0, 2}));
  assert(decode(c, {3, 4, 0, 1, 0, 2}) == Bytes({0, 1, 0, 2}));
  bool rejected = false;
  try {
    decode(c, {3, 2, 0, 1});
  } catch (const Error &e) {
    rejected = e.code == 6005;
  }
  assert(rejected);
  assert(crc16({1, 3, 0, 0, 0, 10}) == 0xcdc5);
  c.function = 15;
  c.quantity = 1;
  c.length = 1;
  c.data[0] = 128;
  rejected = false;
  try {
    encode(c);
  } catch (const Error &e) {
    rejected = e.code == 6007;
  }
  assert(rejected);
}

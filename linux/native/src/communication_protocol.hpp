// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <stdexcept>
#include <vector>
namespace stcommunication {
using Bytes = std::vector<uint8_t>;
struct Error : std::runtime_error {
  unsigned code;
  explicit Error(unsigned c, const char *s) : runtime_error(s), code(c) {}
};
struct Command {
  uint64_t request, action, function, unit, address, quantity, read_address,
      read_quantity, length;
  uint8_t data[512];
};
static_assert(sizeof(Command) == 584);
inline uint16_t crc16(const Bytes &b) {
  uint16_t v = 65535;
  for (auto x : b) {
    v ^= x;
    for (int i = 0; i < 8; ++i)
      v = (v >> 1) ^ ((v & 1) ? 0xa001 : 0);
  }
  return v;
}
inline Bytes encode(const Command &c) {
  auto bad = []() { throw Error(6007, "Invalid Modbus command"); };
  if (c.length > 512 || c.address > 65535)
    bad();
  Bytes p{uint8_t(c.function)};
  auto word = [&](uint64_t n) {
    p.push_back(n >> 8);
    p.push_back(n);
  };
  auto data = [&]() { p.insert(p.end(), c.data, c.data + c.length); };
  auto q = c.quantity, f = c.function;
  if (f >= 1 && f <= 4) {
    if (!q || q > ((f <= 2) ? 2000 : 125) || c.address + q > 65536 || c.length)
      bad();
    word(c.address);
    word(q);
  } else if (f == 5 || f == 6) {
    if (q != 1 || c.length != 2 ||
        (f == 5 && !((c.data[0] == 255 && c.data[1] == 0) ||
                     (c.data[0] == 0 && c.data[1] == 0))))
      bad();
    word(c.address);
    data();
  } else if (f == 15 || f == 16 || f == 23) {
    auto size = f == 15 ? (q + 7) / 8 : q * 2;
    if (!q ||
        q > (f == 15   ? 1968
             : f == 23 ? 121
                       : 123) ||
        c.address + q > 65536 || c.length != size)
      bad();
    if (f == 15 && q % 8 && (c.data[c.length - 1] >> (q % 8)))
      bad();
    if (f == 23) {
      if (c.read_address > 65535 || !c.read_quantity || c.read_quantity > 125 ||
          c.read_address + c.read_quantity > 65536)
        bad();
      word(c.read_address);
      word(c.read_quantity);
    }
    word(c.address);
    word(q);
    p.push_back(c.length);
    data();
  } else if (f == 22) {
    if (q != 1 || c.length != 4)
      bad();
    word(c.address);
    data();
  } else
    bad();
  return p;
}
inline Bytes decode(const Command &c, const Bytes &p) {
  auto expected = encode(c);
  if (p.size() == 2 && p[0] == (c.function | 128) && p[1])
    throw Error(6200 + p[1], "Modbus exception");
  if (p.empty() || p[0] != c.function)
    throw Error(6005, "Function mismatch");
  auto f = c.function;
  if ((f >= 1 && f <= 4) || f == 23) {
    auto q = f == 23 ? c.read_quantity : c.quantity;
    auto size = f <= 2 ? (q + 7) / 8 : q * 2;
    if (p.size() != size + 2 || p[1] != size ||
        (f <= 2 && q % 8 && (p.back() >> (q % 8))))
      throw Error(6005, "Read response");
    return Bytes(p.begin() + 2, p.end());
  }
  if (f == 15 || f == 16)
    expected.resize(5);
  if (p != expected)
    throw Error(6005, "Write echo");
  return {};
}
} // namespace stcommunication

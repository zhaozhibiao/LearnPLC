// SPDX-License-Identifier: MIT
#include "../src/communication_engine.hpp"
#include <cassert>
#include <cstdlib>
#include <cstring>
using namespace stcommunication;
static Json config() {
  return {{"Name", "A"},
          {"Protocol", "Tcp"},
          {"Host", "127.0.0.1"},
          {"Port", 1},
          {"Device", "/dev/ttyS0"},
          {"BaudRate", 9600},
          {"DataBits", 8},
          {"Parity", "None"},
          {"StopBits", 1},
          {"Physical", "RS232"},
          {"Direction", "Automatic"},
          {"BeforeSendMicroseconds", 0},
          {"AfterSendMicroseconds", 0},
          {"TimeoutMilliseconds", 100},
          {"Framing", "Length16"},
          {"DelimiterHex", "0D0A"},
          {"FixedLength", 1},
          {"IdleMilliseconds", 10}};
}
static void read_exact(int fd, uint8_t *data, size_t size) {
  size_t got = 0;
  while (got < size) {
    pollfd p{fd, POLLIN, 0};
    assert(poll(&p, 1, 1000) > 0);
    auto n = ::read(fd, data + got, size - got);
    assert(n > 0);
    got += n;
  }
}
int main() {
  int listener = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
  assert(listener >= 0);
  sockaddr_in address{};
  address.sin_family = AF_INET;
  address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  assert(bind(listener, reinterpret_cast<sockaddr *>(&address),
              sizeof(address)) == 0);
  socklen_t size = sizeof(address);
  assert(getsockname(listener, reinterpret_cast<sockaddr *>(&address), &size) ==
         0);
  assert(listen(listener, 2) == 0);
  auto c = config();
  c["Port"] = ntohs(address.sin_port);
  Channel tcp(c);
  std::thread peer([&] {
    int fd = accept(listener, nullptr, nullptr);
    assert(fd >= 0);
    uint8_t request[5];
    read_exact(fd, request, 5);
    assert(memcmp(request, "\0\3abc", 5) == 0);
    uint8_t reply[] = {0, 2, 'o', 'k', 0, 1, '!'};
    assert(::write(fd, reply, sizeof(reply)) == sizeof(reply));
    ::close(fd);
  });
  Command cmd{};
  cmd.action = 2;
  tcp.execute(cmd, tcp.epoch());
  cmd.action = 3;
  cmd.length = 3;
  memcpy(cmd.data, "abc", 3);
  tcp.execute(cmd, tcp.epoch());
  cmd.action = 4;
  auto first = tcp.execute(cmd, tcp.epoch());
  auto second = tcp.execute(cmd, tcp.epoch());
  assert(first.first == Bytes({'o', 'k'}) && second.first == Bytes({'!'}));
  tcp.close();
  peer.join();
  c["Protocol"] = "ModbusTcp";
  c["Framing"] = "Raw";
  Channel modbus(c);
  std::atomic<unsigned> seen{0};
  std::thread silent([&] {
    int fd = accept(listener, nullptr, nullptr);
    assert(fd >= 0);
    uint8_t request[12];
    read_exact(fd, request, 12);
    ++seen;
    std::this_thread::sleep_for(std::chrono::milliseconds(180));
    pollfd p{fd, POLLIN, 0};
    if (poll(&p, 1, 20) > 0) {
      uint8_t b[64];
      auto n = ::read(fd, b, sizeof(b));
      assert(n == 0);
    }
    ::close(fd);
  });
  cmd = {};
  cmd.action = 1;
  cmd.function = 6;
  cmd.unit = 1;
  cmd.quantity = 1;
  cmd.length = 2;
  cmd.data[1] = 4;
  bool unknown = false;
  try {
    modbus.execute(cmd, modbus.epoch());
  } catch (const Error &e) {
    unknown = e.code == 6008;
  }
  assert(unknown);
  bool poisoned = false;
  try {
    modbus.execute(cmd, modbus.epoch());
  } catch (const Error &e) {
    poisoned = e.code == 6004;
  }
  assert(poisoned && seen == 1);
  silent.join();
  modbus.close();
  ::close(listener);
  int master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
  assert(master >= 0 && grantpt(master) == 0 && unlockpt(master) == 0);
  char *device = ptsname(master);
  assert(device);
  int slave_guard = ::open(device, O_RDWR | O_NOCTTY | O_CLOEXEC);
  assert(slave_guard >= 0);
  c = config();
  c["Protocol"] = "Serial";
  c["Device"] = device;
  c["Framing"] = "Delimiter";
  Channel serial(c);
  std::thread serial_peer([&] {
    uint8_t b[5];
    read_exact(master, b, 5);
    assert(memcmp(b, "abc\r\n", 5) == 0);
    const char reply[] = "one\r\ntwo\r\n";
    assert(::write(master, reply, sizeof(reply) - 1) == sizeof(reply) - 1);
  });
  cmd = {};
  cmd.action = 3;
  cmd.length = 3;
  memcpy(cmd.data, "abc", 3);
  serial.execute(cmd, serial.epoch());
  cmd.action = 4;
  assert(serial.execute(cmd, serial.epoch()).first == Bytes({'o', 'n', 'e'}));
  assert(serial.execute(cmd, serial.epoch()).first == Bytes({'t', 'w', 'o'}));
  serial_peer.join();
  serial.close();
  ::close(slave_guard);
  ::close(master);
}

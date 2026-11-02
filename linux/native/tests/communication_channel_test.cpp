// SPDX-License-Identifier: MIT
#include "../src/communication_engine.hpp"
#include <cassert>
#include <cstring>
using namespace stcommunication;
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
  assert(listen(listener, 1) == 0);
  Json config = {{"Name", "A"},
                 {"Protocol", "ModbusTcp"},
                 {"Host", "127.0.0.1"},
                 {"Port", ntohs(address.sin_port)},
                 {"Device", "/dev/ttyS0"},
                 {"BaudRate", 9600},
                 {"DataBits", 8},
                 {"Parity", "Even"},
                 {"StopBits", 1},
                 {"Physical", "RS232"},
                 {"Direction", "Automatic"},
                 {"BeforeSendMicroseconds", 0},
                 {"AfterSendMicroseconds", 0},
                 {"TimeoutMilliseconds", 500},
                 {"Framing", "Raw"},
                 {"DelimiterHex", "0D0A"},
                 {"FixedLength", 1},
                 {"IdleMilliseconds", 10}};
  Json manifest = {
      {"SchemaVersion", 1},
      {"InterfaceVersion", "workbench-communication-v1"},
      {"PlcIntervalMilliseconds", 10},
      {"Channels", Json::array({{{"Index", 1}, {"Configuration", config}}})}};
  assert(validate(manifest).second.size() == 1);
  auto rejects_manifest = [](const Json &value) {
    bool bad = false;
    try {
      validate(value);
    } catch (...) {
      bad = true;
    }
    assert(bad);
  };
  auto invalid_number = manifest;
  invalid_number["SchemaVersion"] = 1.0;
  rejects_manifest(invalid_number);
  invalid_number = manifest;
  invalid_number["Channels"][0]["Index"] = 1.0;
  rejects_manifest(invalid_number);
  invalid_number = manifest;
  invalid_number["PlcIntervalMilliseconds"] = uint64_t(0x10000000aULL);
  rejects_manifest(invalid_number);
  invalid_number = manifest;
  invalid_number["Channels"][0]["Configuration"]["Port"] =
      uint64_t(0x100000001ULL);
  rejects_manifest(invalid_number);
  auto bad = manifest;
  bad["Channels"][0]["Configuration"]["Host"] = "localhost";
  bool rejected = false;
  try {
    validate(bad);
  } catch (...) {
    rejected = true;
  }
  assert(rejected);
  std::thread server([&] {
    int fd = accept(listener, nullptr, nullptr);
    assert(fd >= 0);
    uint8_t request[12];
    size_t got = 0;
    while (got < 12) {
      auto n = ::read(fd, request + got, 12 - got);
      assert(n > 0);
      got += n;
    }
    assert(request[7] == 3 && request[11] == 1);
    uint8_t reply[] = {request[0], request[1], 0, 0, 0, 5, 1, 3, 2, 0x12, 0x34};
    for (auto byte : reply) {
      assert(::write(fd, &byte, 1) == 1);
    }
    ::close(fd);
  });
  Channel channel(config);
  Command command{};
  command.request = 1;
  command.action = 1;
  command.unit = 1;
  command.function = 3;
  command.quantity = 1;
  auto [data, ack] = channel.execute(command, channel.epoch());
  assert(ack && data == Bytes({0x12, 0x34}));
  channel.close();
  server.join();
  ::close(listener);
  Engine engine({config}, 10);
  Bytes image(16 + 16 * sizeof(Command));
  uint64_t scan = 1, flags = 1;
  memcpy(image.data(), &scan, 8);
  memcpy(image.data() + 8, &flags, 8);
  command.request = 9;
  memcpy(image.data() + 16 + 15 * sizeof(Command), &command, sizeof(command));
  auto output = engine.image(image);
  Feedback feedback{};
  memcpy(&feedback, output.data() + 15 * sizeof(Feedback), sizeof(feedback));
  assert(feedback.request == 9 && feedback.error == 6001);
}

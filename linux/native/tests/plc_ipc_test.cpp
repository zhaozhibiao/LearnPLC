// SPDX-License-Identifier: MIT
#include "stworkbench/plc_ipc.hpp"
#include <cassert>
#include <sys/socket.h>
#include <sys/un.h>
#include <thread>
#include <unistd.h>
using namespace stworkbench;
int main() {
  const auto path = "/tmp/st-plc-ipc-" + std::to_string(getpid());
  Fd listener(socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0));
  sockaddr_un address{};
  address.sun_family = AF_UNIX;
  std::copy(path.begin(), path.end(), address.sun_path);
  assert(bind(listener.get(), reinterpret_cast<sockaddr *>(&address),
              sizeof address) == 0);
  assert(listen(listener.get(), 4) == 0);
  std::thread server([&] {
    Fd peer(accept(listener.get(), nullptr, nullptr));
    char buf[100];
    auto n = read(peer.get(), buf, sizeof buf);
    assert(std::string(buf, n) == "STATUS\n");
    assert(write(peer.get(), "STATUS:", 7) == 7);
    usleep(1000);
    assert(write(peer.get(), "RUNNING\n", 8) == 8);
    n = read(peer.get(), buf, sizeof buf);
    assert(std::string(buf, n) == "START\n");
    // Ambiguous delivery must close the connection, never replay START.
    usleep(70000);
    peer.reset();
    peer.reset(accept(listener.get(), nullptr, nullptr));
    n = read(peer.get(), buf, sizeof buf);
    assert(std::string(buf, n) == "PING\n");
    assert(write(peer.get(), "PONG\n", 5) == 5);
  });
  PlcIpc client(path);
  assert(client.request("STATUS", std::chrono::milliseconds(200)) ==
         "STATUS:RUNNING");
  bool failed = false;
  try {
    client.request("START", std::chrono::milliseconds(20));
  } catch (const std::exception &) {
    failed = true;
  }
  assert(failed);
  assert(client.request("PING", std::chrono::milliseconds(200)) == "PONG");
  failed = false;
  try {
    client.request("STOP\nSTART");
  } catch (const std::exception &) {
    failed = true;
  }
  assert(failed);
  server.join();
  unlink(path.c_str());
}

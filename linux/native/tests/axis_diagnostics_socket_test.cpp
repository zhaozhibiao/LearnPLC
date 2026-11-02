// SPDX-License-Identifier: MIT
#include "../src/axis_diagnostics.hpp"
#include <cassert>
int main() {
  using namespace stworkbench;
  using namespace diagnostics;
  auto path = (std::filesystem::temp_directory_path() /
               ("st-probe-test-" + std::to_string(::getpid()) + ".sock"))
                  .string();
  struct Cleanup {
    std::string p;
    ~Cleanup() { ::unlink(p.c_str()); }
  } cleanup{path};
  Fd server(::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0));
  assert(server);
  sockaddr_un address{};
  address.sun_family = AF_UNIX;
  std::memcpy(address.sun_path, path.c_str(), path.size() + 1);
  assert(::bind(server.get(), reinterpret_cast<sockaddr *>(&address),
                sizeof address) == 0);
  assert(::listen(server.get(), 1) == 0);
  std::exception_ptr failure;
  std::thread responder([&] {
    try {
      Fd peer(::accept4(server.get(), nullptr, nullptr, SOCK_CLOEXEC));
      assert(peer);
      for (int i = 1; i <= 3; ++i) {
        auto request =
            hardware::object(hardware::frame_read(peer.get(), 1048576));
        assert(request["sequence"] == i);
        assert(request["kind"] ==
               (i == 1 ? "HelloRequest" : "ReadSnapshotRequest"));
        auto response = request;
        response["kind"] = i == 1 ? "HelloResponse" : "SnapshotResponse";
        response["payload"] = Json::object();
        if (i == 3)
          response["sequence"] = 1;
        hardware::frame_write(peer.get(), response);
      }
    } catch (...) {
      failure = std::current_exception();
    }
  });
  ReadonlyNativeClient client(path);
  client.request("HelloRequest", Json::object(), "HelloResponse");
  bool denied = false;
  try {
    client.request("AcquireLeaseRequest", Json::object(),
                   "AcquireLeaseResponse");
  } catch (...) {
    denied = true;
  }
  assert(denied);
  client.request("ReadSnapshotRequest", Json::object(), "SnapshotResponse");
  bool poisoned = false;
  try {
    client.request("ReadSnapshotRequest", Json::object(), "SnapshotResponse");
  } catch (...) {
    poisoned = true;
  }
  assert(poisoned);
  responder.join();
  if (failure)
    std::rethrow_exception(failure);
  poisoned = false;
  try {
    client.request("ReadSnapshotRequest", Json::object(), "SnapshotResponse");
  } catch (...) {
    poisoned = true;
  }
  assert(poisoned);
}

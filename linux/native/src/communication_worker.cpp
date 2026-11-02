// SPDX-License-Identifier: MIT
#include "communication_engine.hpp"
#include <csignal>
#include <iostream>
#include <sys/un.h>
using namespace stworkbench;
using namespace stcommunication;
namespace {
volatile sig_atomic_t running = 1;
void terminate(int) { running = 0; }
} // namespace
int main(int argc, char **argv) {
  try {
    std::string manifest, path, capability;
    for (int i = 1; i < argc; ++i) {
      std::string option = argv[i];
      if (i + 1 >= argc)
        throw std::runtime_error("Missing option value");
      if (option == "--manifest")
        manifest = argv[++i];
      else if (option == "--socket")
        path = argv[++i];
      else if (option == "--capabilities")
        capability = argv[++i];
      else
        throw std::runtime_error("Unknown option");
    }
    if (manifest.empty() || path.empty())
      throw std::runtime_error("--manifest and --socket required");
    auto raw = read_file(manifest, 131072);
    auto [period, configs] = validate(parse_json(raw));
    Engine engine(configs, period);
    auto parent =
        std::filesystem::absolute(std::filesystem::path(path).parent_path());
    if (std::filesystem::create_directories(parent) &&
        chmod(parent.c_str(), 0700))
      throw std::runtime_error("Private socket directory creation failed");
    struct stat info{};
    if (lstat(parent.c_str(), &info) || !S_ISDIR(info.st_mode) ||
        info.st_uid != getuid() || (info.st_mode & 0777) != 0700)
      throw std::runtime_error(
          "Socket directory must be owned private 0700 directory");
    if (lstat(path.c_str(), &info) == 0)
      throw std::runtime_error("Socket already exists");
    if (capability.empty())
      capability = path + ".capabilities.json";
    if (std::filesystem::canonical(
            std::filesystem::path(capability).parent_path()) !=
        std::filesystem::canonical(parent))
      throw std::runtime_error("Capability directory differs");
    Fd server(socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0));
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    if (path.size() >= sizeof(address.sun_path))
      throw std::runtime_error("Socket path too long");
    memcpy(address.sun_path, path.c_str(), path.size() + 1);
    if (bind(server.get(), reinterpret_cast<sockaddr *>(&address),
             sizeof(address)))
      throw std::runtime_error("Socket bind");
    struct Cleanup {
      std::string socket, capability;
      ~Cleanup() {
        unlink(socket.c_str());
        unlink(capability.c_str());
      }
    } cleanup{path, capability};
    if (chmod(path.c_str(), 0600) || listen(server.get(), 1))
      throw std::runtime_error("Socket listen");
    signal(SIGTERM, terminate);
    signal(SIGINT, terminate);
    signal(SIGPIPE, SIG_IGN);
    Fd client;
    Bytes incoming, outgoing;
    size_t offset = 0;
    double last_cap = 0;
    auto disconnect = [&] {
      client.reset();
      incoming.clear();
      outgoing.clear();
      offset = 0;
      engine.close();
    };
    while (running) {
      engine.tick();
      double now = clock_now();
      if (now - last_cap > .1) {
        atomic_write(capability,
                     Json({{"SchemaVersion", 1},
                           {"InterfaceVersion", "workbench-communication-v1"},
                           {"ManifestSha256", sha256(raw)},
                           {"PID", getpid()},
                           {"WorkerReady", true},
                           {"LastScan", engine.scan},
                           {"ScanHealthy", engine.healthy}})
                         .dump(),
                     0600);
        last_cap = now;
      }
      pollfd fds[2] = {
          {server.get(), POLLIN, 0},
          {client.get(), short(POLLIN | (outgoing.empty() ? 0 : POLLOUT)), 0}};
      int n = poll(fds, 2, 20);
      if (n < 0) {
        if (errno == EINTR)
          continue;
        throw std::runtime_error("Poll failed");
      }
      if (fds[0].revents & POLLIN) {
        Fd accepted(accept4(server.get(), nullptr, nullptr,
                            SOCK_NONBLOCK | SOCK_CLOEXEC));
        if (accepted) {
          ucred peer{};
          socklen_t size = sizeof(peer);
          if (!getsockopt(accepted.get(), SOL_SOCKET, SO_PEERCRED, &peer,
                          &size) &&
              peer.uid == getuid() && !client)
            client = std::move(accepted);
        }
      }
      if (!client)
        continue;
      bool bad = false;
      if (fds[1].revents & POLLOUT) {
        ssize_t sent = send(client.get(), outgoing.data() + offset,
                            outgoing.size() - offset, MSG_NOSIGNAL);
        if (sent > 0) {
          offset += sent;
          if (offset == outgoing.size()) {
            outgoing.clear();
            offset = 0;
          }
        } else if (sent == 0 || (errno != EAGAIN && errno != EINTR))
          bad = true;
      }
      if (fds[1].revents & POLLIN) {
        constexpr size_t size = 16 + 16 * sizeof(Command);
        uint8_t bytes[size];
        ssize_t got = recv(client.get(), bytes, size - incoming.size(), 0);
        if (got > 0) {
          incoming.insert(incoming.end(), bytes, bytes + got);
          if (incoming.size() == size) {
            if (!outgoing.empty())
              bad = true;
            else {
              outgoing = engine.image(incoming);
              incoming.clear();
            }
          }
        } else if (got == 0 || (errno != EAGAIN && errno != EINTR))
          bad = true;
      }
      if (fds[1].revents & (POLLERR | POLLHUP | POLLNVAL))
        bad = true;
      if (bad)
        disconnect();
    }
    return 0;
  } catch (const std::exception &e) {
    std::cerr << e.what() << '\n';
    return 1;
  }
}

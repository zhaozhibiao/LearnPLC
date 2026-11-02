// SPDX-License-Identifier: MIT
#include "stworkbench/common.hpp"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <iostream>
#include <map>
#include <poll.h>
#include <signal.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
using namespace stworkbench;
namespace {
volatile sig_atomic_t alive = 1;
void stop(int) { alive = 0; }
struct State {
  uint64_t request = 0, busy = 0, done = 0, error = 0;
  double progress = 0;
};
struct Command {
  uint64_t request, id, version;
  char digest[64];
  uint64_t scan, flags;
};
static_assert(sizeof(Command) == 104);
static_assert(sizeof(State) == 40);
struct Engine {
  std::map<std::pair<uint64_t, uint64_t>, Json> assets;
  int owner = -1;
  uint64_t scan = 0;
  bool scanned = false, bound = false;
  Command binding{};
  State state;
  double last = 0, started = 0, watchdog;
  size_t index = 0;
  Json trajectory;
  Engine(const Json &manifest, double w) : watchdog(w) {
    if (manifest.at("SchemaVersion") != 1 ||
        manifest.at("Backend") != "VirtualContinuous" || !std::isfinite(w) ||
        w < .01 || w > 5)
      throw std::runtime_error("invalid virtual manifest/backend/watchdog");
    const auto &paths = manifest.at("Paths");
    if (!paths.is_array() || paths.size() > 256)
      throw std::runtime_error("asset bound");
    size_t total = 0;
    std::map<uint64_t, std::string> ids;
    for (const auto &p : paths) {
      const auto &a = p.at("Asset");
      const auto &t = p.at("Trajectory");
      uint64_t id = a.at("NumericId"), v = a.at("Version");
      std::string digest = a.at("ContentHash"), name = a.at("Id");
      auto axes = a.at("Axes");
      if (a.at("Mode") != "Continuous" || a.at("Unit") != "mm" || !id ||
          id >= (1ULL << 63) || !v || v > UINT32_MAX || digest.size() != 64 ||
          digest.find_first_not_of("0123456789abcdef") != std::string::npos ||
          axes.size() < 2 || axes.size() > 3)
        throw std::runtime_error("path identity");
      for (size_t i = 0; i < axes.size(); ++i)
        for (size_t j = 0; j < i; ++j)
          if (axes[i] == axes[j])
            throw std::runtime_error("duplicate axis");
      if (assets.count({id, v}) || (ids.count(id) && ids[id] != name))
        throw std::runtime_error("path collision");
      ids[id] = name;
      const auto &s = t.at("Samples");
      total += s.size();
      double duration = t.at("DurationSeconds"),
             cycle = a.at("Profile").at("CycleSeconds"), previous = -1;
      if (s.size() < 2 || s.size() > 250000 || total > 1000000 ||
          !std::isfinite(duration) || duration <= 0 || !std::isfinite(cycle) ||
          cycle < .0001 || cycle > .1)
        throw std::runtime_error("trajectory bound");
      for (size_t i = 0; i < s.size(); ++i) {
        double n = s[i].at("TimeSeconds");
        if (!std::isfinite(n) || n <= previous || (i == 0 && n != 0) ||
            (i && n - previous > cycle * 1.01))
          throw std::runtime_error("sample time");
        for (auto f : {"Position", "Velocity", "Acceleration"})
          for (auto axis : {"X", "Y", "Z"})
            if (!std::isfinite(s[i].at(f).at(axis).get<double>()))
              throw std::runtime_error("sample finite");
        previous = n;
      }
      if (std::abs(previous - duration) > 1e-9)
        throw std::runtime_error("duration mismatch");
      assets[{id, v}] = p;
    }
  }
  void cancel(uint64_t e) {
    state.busy = state.done = 0;
    state.error = e;
  }
  State command(int client, const Command &c, double now) {
    if (owner != -1 && owner != client)
      return {c.request, 0, 0, 1004, 0};
    owner = client;
    if (!scanned || c.scan > scan) {
      scan = c.scan;
      last = now;
      scanned = true;
    } else if (c.scan < scan) {
      cancel(1002);
      return state;
    }
    if (!(c.flags & 1) || (c.flags & 2)) {
      if (state.busy)
        cancel(1003);
      return state;
    }
    if (!c.request)
      return state;
    if (bound && c.request == binding.request) {
      if (c.id != binding.id || c.version != binding.version ||
          memcmp(c.digest, binding.digest, 64))
        return {c.request, 0, 0, 1007, 0};
      return state;
    }
    if (state.busy)
      return {c.request, 0, 0, 1008, 0};
    if (bound && c.request < binding.request)
      return {c.request, 0, 0, 1007, 0};
    binding = c;
    bound = true;
    auto p = assets.find({c.id, c.version});
    if (p == assets.end() ||
        p->second["Asset"]["ContentHash"] != std::string(c.digest, 64)) {
      state = {c.request, 0, 0, 1001, 0};
      return state;
    }
    trajectory = p->second["Trajectory"];
    index = 0;
    started = now;
    state = {c.request, 1, 0, 0, 0};
    return state;
  }
  void tick(double now) {
    if (!state.busy)
      return;
    if (now - last > watchdog) {
      cancel(1002);
      return;
    }
    auto &s = trajectory["Samples"];
    while (index < s.size() && s[index]["TimeSeconds"].get<double>() <=
                                   std::max(0., now - started) + 1e-12)
      ++index;
    state.progress =
        std::min(1., s[index ? index - 1 : 0]["TimeSeconds"].get<double>() /
                         trajectory["DurationSeconds"].get<double>());
    if (index == s.size()) {
      state.busy = 0;
      state.done = 1;
    }
  }
  void release(int client) {
    if (owner == client) {
      if (state.busy)
        cancel(1005);
      owner = -1;
      scanned = bound = false;
    }
  }
};
double now() { return monotonic_ns() / 1e9; }
} // namespace
#ifndef ST_PATH_NO_MAIN
int main(int argc, char **argv) {
  std::string manifest, path;
  double watchdog = .25;
  try {
    for (int i = 1; i < argc; ++i) {
      std::string a = argv[i];
      if (i + 1 >= argc)
        throw std::runtime_error("missing option value");
      if (a == "--manifest")
        manifest = argv[++i];
      else if (a == "--socket")
        path = argv[++i];
      else if (a == "--watchdog")
        watchdog = std::stod(argv[++i]);
      else
        throw std::runtime_error("unknown option");
    }
    if (manifest.empty() || path.empty())
      throw std::runtime_error("--manifest and --socket required");
    auto bytes = read_file(manifest, 256 * 1024 * 1024);
    auto m = parse_json(bytes);
    Engine engine(m, watchdog);
    path = std::filesystem::absolute(path).string();
    std::string side = path + ".capabilities.json";
    struct stat st{};
    if (lstat(path.c_str(), &st) == 0 || lstat(side.c_str(), &st) == 0)
      throw std::runtime_error("endpoint exists");
    std::filesystem::create_directories(
        std::filesystem::path(path).parent_path());
    Fd server(socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0));
    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    if (path.size() >= sizeof(addr.sun_path))
      throw std::runtime_error("socket path too long");
    memcpy(addr.sun_path, path.c_str(), path.size() + 1);
    if (bind(server.get(), reinterpret_cast<sockaddr *>(&addr), sizeof(addr)) ||
        chmod(path.c_str(), 0600) || listen(server.get(), 4))
      throw std::runtime_error("socket bind/listen");
    struct Cleanup {
      std::string p, s;
      ~Cleanup() {
        unlink(p.c_str());
        if (!s.empty())
          unlink(s.c_str());
      }
    } cleanup{path, {}};
    Json caps = {{"SchemaVersion", 1},
                 {"Backend", "VirtualContinuous"},
                 {"PID", getpid()},
                 {"PhysicalContinuous", false},
                 {"ManifestSha256", sha256(bytes)},
                 {"Paths", Json::array()}};
    for (auto &p : m["Paths"]) {
      Json a;
      for (auto key : {"Id", "NumericId", "Version", "ContentHash", "Axes"})
        a[key] = p["Asset"][key];
      caps["Paths"].push_back(a);
    }
    std::string tmp = side + "." + std::to_string(getpid()) + ".tmp";
    atomic_write(tmp, caps.dump(), 0600);
    if (link(tmp.c_str(), side.c_str())) {
      unlink(tmp.c_str());
      throw std::runtime_error("capability publication");
    }
    unlink(tmp.c_str());
    cleanup.s = side;
    signal(SIGTERM, stop);
    signal(SIGINT, stop);
    signal(SIGPIPE, SIG_IGN);
    std::cout << Json({{"Backend", "VirtualContinuous"},
                       {"PhysicalContinuous", false},
                       {"Socket", path},
                       {"Assets", engine.assets.size()}})
                     .dump()
              << std::endl;
    std::map<int, std::vector<char>> clients;
    while (alive) {
      std::vector<pollfd> fds{{server.get(), POLLIN, 0}};
      for (auto &[fd, b] : clients)
        fds.push_back({fd, POLLIN, 0});
      if (poll(fds.data(), fds.size(), 1) < 0 && errno != EINTR)
        throw std::runtime_error("poll");
      if (fds[0].revents & POLLIN) {
        int c = accept4(server.get(), nullptr, nullptr,
                        SOCK_NONBLOCK | SOCK_CLOEXEC);
        if (c >= 0) {
          if (clients.size() < 4)
            clients[c] = {};
          else
            close(c);
        }
      }
      for (size_t i = 1; i < fds.size(); ++i) {
        int fd = fds[i].fd;
        if (!fds[i].revents)
          continue;
        auto &b = clients[fd];
        char data[104];
        ssize_t n = recv(fd, data, 104 - b.size(), 0);
        bool bad = n <= 0;
        if (n > 0) {
          b.insert(b.end(), data, data + n);
          if (b.size() == 104) {
            Command c;
            memcpy(&c, b.data(), 104);
            b.clear();
            State result = engine.command(fd, c, now());
            engine.tick(now());
            if (!result.error)
              result = engine.state;
            if (send(fd, &result, sizeof(result), MSG_NOSIGNAL) !=
                sizeof(result))
              bad = true;
          }
        }
        if (bad) {
          engine.release(fd);
          close(fd);
          clients.erase(fd);
        }
      }
      engine.tick(now());
    }
    for (auto &[fd, b] : clients)
      close(fd);
    return 0;
  } catch (const std::exception &e) {
    std::cerr << e.what() << '\n';
    return 1;
  }
}
#endif

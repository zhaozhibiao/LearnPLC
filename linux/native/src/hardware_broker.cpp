// SPDX-License-Identifier: MIT
#include "hardware_operations.hpp"
namespace stworkbench::hardware {
inline bool authorized(uid_t uid) {
  if (uid == 0)
    return true;
  auto p = ::getpwuid(uid);
  auto g = ::getgrnam("stworkbench-hardware");
  if (!p || !g)
    return false;
  int count = 0;
  ::getgrouplist(p->pw_name, p->pw_gid, nullptr, &count);
  require(count > 0 && count <= 65536, "Invalid group count");
  std::vector<gid_t> groups(count);
  if (::getgrouplist(p->pw_name, p->pw_gid, groups.data(), &count) < 0)
    return false;
  return std::find(groups.begin(), groups.end(), g->gr_gid) != groups.end();
}
class Broker {
  std::set<std::pair<uid_t, std::string>> consumed;

public:
  Broker() {
    root_owned(fs::path(ledger_path).parent_path());
    struct stat s{};
    if (::lstat(ledger_path, &s) == 0) {
      root_owned(ledger_path, 0600);
      auto j = object(capture(ledger_path, 1024 * 1024));
      require(j.size() == 1 && j.contains("entries") &&
                  j["entries"].is_array() && j["entries"].size() <= 4096,
              "Invalid apply ledger");
      for (auto &e : j["entries"]) {
        require(e.is_array() && e.size() == 2 && integer(e[0]) && e[0] >= 0 &&
                    e[0] <= UINT32_MAX && e[1].is_string(),
                "Invalid ledger identity");
        request({{"protocolVersion", 1},
                 {"requestId", e[1]},
                 {"action", "status"}});
        consumed.emplace(e[0].get<uid_t>(), e[1].get<std::string>());
      }
    } else
      require(errno == ENOENT, "Cannot inspect apply ledger");
  }
  Json dispatch(Json r, uid_t uid) {
    request(r);
    auto action = r["action"];
    Json result;
    bool success = true;
    if (action == "apply") {
      auto key = std::make_pair(uid, r["requestId"].get<std::string>());
      require(!consumed.contains(key),
              "Apply request consumed; query actual status");
      require(consumed.size() < 4096, "Apply ledger capacity reached");
      consumed.insert(key);
      Json entries = Json::array();
      for (auto &[u, id] : consumed)
        entries.push_back(Json::array({u, id}));
      root_owned(fs::path(ledger_path).parent_path());
      atomic_write(ledger_path, Json{{"entries", entries}}.dump(), 0600);
      result = apply(r["buildHash"]);
      success = result.value("status", Json()) == "applied" &&
                result.value("verified", Json()) == true;
    } else if (action == "validate") {
      auto i = installation();
      auto b = candidate(identity(r["buildHash"]));
      check_binary(i, b.required);
      result = b.proof;
    } else {
      result = {{"plcState", plc_status()}, {"nativeReady", false}};
      try {
        auto hello = native(
            "HelloRequest",
            {{"clientName", "hardware-broker-status"}, {"clientVersion", "1"}},
            "HelloResponse");
        auto snapshot =
            native("ReadSnapshotRequest", {{"includeDiagnostics", false}},
                   "SnapshotResponse");
        result.update({{"nativeReady",
                        hello.value("runtimeReady", Json()) == true &&
                            snapshot.value("runtimeReady", Json()) == true},
                       {"topologyFingerprint",
                        hello.value("ethercatTopologyFingerprint", Json())},
                       {"snapshot", snapshot}});
      } catch (...) {
        result["nativeState"] = "unavailable-or-unknown";
      }
    }
    return {{"protocolVersion", 1},
            {"requestId", r["requestId"]},
            {"success", success},
            {"result", result}};
  }
};
} // namespace stworkbench::hardware
int main(int argc, char **) {
  using namespace stworkbench;
  using namespace hardware;
  try {
    require(argc == 1 && ::geteuid() == 0,
            "Root broker service requires no arguments");
    auto pid = ::getenv("LISTEN_PID"), fds = ::getenv("LISTEN_FDS");
    require(pid && fds && std::string(pid) == std::to_string(::getpid()) &&
                std::string(fds) == "1",
            "Exactly one systemd activation socket required");
    sockaddr_un a{};
    socklen_t n = sizeof a;
    int accept = 0;
    socklen_t len = sizeof accept;
    require(
        ::getsockname(3, reinterpret_cast<sockaddr *>(&a), &n) == 0 &&
            a.sun_family == AF_UNIX && n > offsetof(sockaddr_un, sun_path) &&
            n <= sizeof a &&
            std::string(a.sun_path, strnlen(a.sun_path, sizeof a.sun_path)) ==
                broker_socket &&
            ::getsockopt(3, SOL_SOCKET, SO_ACCEPTCONN, &accept, &len) == 0 &&
            accept,
        "Unexpected activation socket");
    ::unsetenv("LISTEN_PID");
    ::unsetenv("LISTEN_FDS");
    require(::fcntl(3, F_SETFD, FD_CLOEXEC) == 0,
            "Activation socket protection failed");
    Broker broker;
    for (;;) {
      Fd stream(::accept4(3, nullptr, nullptr, SOCK_CLOEXEC));
      if (!stream) {
        if (errno == EINTR)
          continue;
        throw std::runtime_error("Accept failed");
      }
      Json req = Json::object(), response;
      try {
        ucred peer{};
        socklen_t l = sizeof peer;
        require(::getsockopt(stream.get(), SOL_SOCKET, SO_PEERCRED, &peer,
                             &l) == 0 &&
                    l == sizeof peer && authorized(peer.uid),
                "Unauthorized hardware caller");
        req = request(object(frame_read(stream.get(), 4096)));
        response = broker.dispatch(req, peer.uid);
      } catch (const std::exception &e) {
        response = {{"protocolVersion", 1},
                    {"requestId", req.value("requestId", Json())},
                    {"success", false},
                    {"message", std::string(e.what()).substr(0, 512)}};
      }
      try {
        frame_write(stream.get(), response);
      } catch (...) {
      }
    }
  } catch (const std::exception &e) {
    std::cerr << e.what() << '\n';
    return 1;
  }
}

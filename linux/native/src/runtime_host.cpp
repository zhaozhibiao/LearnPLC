// SPDX-License-Identifier: MIT
#include "runtime_http.hpp"
#include "stworkbench/plc_ipc.hpp"
#include "stworkbench/startup_gate.hpp"
#include <csignal>
#include <iostream>
#include <spawn.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <sys/wait.h>
extern char **environ;
namespace stworkbench::runtime {
static constexpr auto Prefix =
    "/home/a/.local/share/st-workbench/runtime-v4.3.0";
class Logs {
  std::mutex mutex_;
  std::deque<Json> entries_;
  uint64_t next_ = 1;

public:
  void append(const std::string &line) {
    if (line.empty())
      return;
    Json value;
    try {
      value = parse_json(line);
      if (!value.is_object() || !value.value("message", Json{}).is_string())
        value = nullptr;
    } catch (...) {
    }
    if (value.is_null()) {
      std::string message = line, level = "INFO";
      auto close = line.find(']');
      if (line.starts_with("[") && close < 16) {
        level = line.substr(1, close - 1);
        message = line.substr(close + 1);
      }
      value = {{"message", message}, {"level", level}};
    }
    if (value.value("level", Json{}) == "WARN")
      value["level"] = "WARNING";
    value["timestamp"] = std::to_string(std::time(nullptr));
    std::lock_guard lock(mutex_);
    value["id"] = next_++;
    entries_.push_back(value);
    if (entries_.size() > 1000)
      entries_.pop_front();
  }
  Json get(uint64_t id, const std::string &level) {
    std::lock_guard lock(mutex_);
    Json out = Json::array();
    for (auto &entry : entries_)
      if (entry["id"].get<uint64_t>() >= id &&
          (level.empty() || entry.value("level", Json{}) == level))
        out.push_back(entry);
    return out;
  }
  void listen(const std::filesystem::path &path) {
    check(path.string().size() < sizeof(sockaddr_un::sun_path),
          "Log socket path too long");
    struct stat info{};
    if (::lstat(path.c_str(), &info) == 0) {
      check(S_ISSOCK(info.st_mode) && info.st_uid == geteuid(),
            "Unsafe existing log socket");
      Fd probe(::socket(AF_UNIX,SOCK_STREAM|SOCK_CLOEXEC|SOCK_NONBLOCK,0));
      sockaddr_un endpoint{};endpoint.sun_family=AF_UNIX;const auto address=path.string();std::copy(address.begin(),address.end(),endpoint.sun_path);
      const auto connected=::connect(probe.get(),reinterpret_cast<sockaddr *>(&endpoint),sizeof endpoint);
      check(connected!=0&&errno==ECONNREFUSED,"Another Runtime log owner may still be active");
      ::unlink(path.c_str());
    }
    Fd server(::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0));
    check(bool(server), "Log socket unavailable");
    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    auto name = path.string();
    std::copy(name.begin(), name.end(), addr.sun_path);
    check(::bind(server.get(), reinterpret_cast<sockaddr *>(&addr),
                 sizeof addr) == 0 &&
              ::chmod(path.c_str(), 0600) == 0 &&
              ::listen(server.get(), 4) == 0,
          "Cannot bind log socket");
    std::thread([this, server = std::move(server)]() mutable {
      for (;;) {
        Fd client(::accept4(server.get(), nullptr, nullptr, SOCK_CLOEXEC));
        if (!client)
          continue;
        std::string pending;
        char bytes[8192];
        for (;;) {
          auto n = ::read(client.get(), bytes, sizeof bytes);
          if (n <= 0)
            break;
          pending.append(bytes, n);
          if (pending.size() > 65536) {
            append("[ERROR] Native log line exceeded capacity");
            break;
          }
          for (auto at = pending.find('\n'); at != pending.npos;
               at = pending.find('\n')) {
            append(pending.substr(0, at));
            pending.erase(0, at + 1);
          }
        }
      }
    }).detach();
  }
};
static std::atomic<pid_t> child_pid = -1;
static void terminate(int signal) {
  auto pid = child_pid.load();
  if (pid > 0)
    ::kill(pid, SIGTERM);
  ::_exit(128 + signal);
}
inline std::string strip(std::string s) {
  while (!s.empty() &&
         (s.back() == '\n' || s.back() == '\r' || s.back() == ' '))
    s.pop_back();
  return s;
}
inline Json debug_request(PlcIpc &ipc, const Json &input) {
  check(input.is_object() && input.value("command", Json{}).is_string(),
        "Invalid debug command");
  auto command = input["command"].get<std::string>();
  check(!command.empty() && command.size() <= 3 * 4096,
        "Invalid debug command capacity");
  for (unsigned char c : command)
    check(std::isxdigit(c) || c == ' ', "Invalid debug command encoding");
  auto reply = ipc.request("DEBUG:" + command, std::chrono::seconds(2));
  if (reply.starts_with("DEBUG:ERROR"))
    return {{"success", false}, {"error", "Native debug operation rejected"}};
  check(reply.starts_with("DEBUG:"), "Unexpected debug reply");
  return {{"success", true}, {"data", strip(reply.substr(6))}};
}
int host_main(int argc, char **argv) {
  ::umask(0077);
  std::filesystem::path prefix = Prefix;
  unsigned port = 8443;
  bool no_spawn = false;
  for (int i = 1; i < argc; i++) {
    std::string arg = argv[i];
    if (arg == "--no-spawn")
      no_spawn = true;
    else if (arg == "--port" && i + 1 < argc) {
      port = std::stoul(argv[++i]);
      check(port > 0 && port <= 65535, "Invalid port");
    } else
      throw std::runtime_error("Unknown Runtime host option");
  }
  Auth auth(prefix / "data");
  PlcIpc ipc((prefix / "run/plc_runtime.socket").string());
  Logs logs;
  if (!no_spawn) {
    validate_startup(prefix);
    for(const auto &entry:std::filesystem::directory_iterator("/proc",std::filesystem::directory_options::skip_permission_denied)) {
      const auto name=entry.path().filename().string();if(name.empty()||name.find_first_not_of("0123456789")!=name.npos)continue;
      std::error_code error;auto exe=std::filesystem::read_symlink(entry.path()/"exe",error).string();if(error)continue;
      auto expected=(prefix/"source/build/plc_main").string();check(exe!=expected&&exe!=expected+" (deleted)","Native PLC already running; refuse a second IPC owner");
    }
    logs.listen(prefix / "run/log_runtime.socket");
    ::setenv("OPENPLC_RUNTIME_DIR", (prefix / "run").c_str(), 1);
    ::setenv("OPENPLC_PERSISTENT_DATA_DIR", (prefix / "data").c_str(), 1);
    ::signal(SIGTERM, terminate);
    ::signal(SIGINT, terminate);
    std::thread([prefix, &logs] {
      for (;;) {
        try {
          validate_startup(prefix);
          auto binary = (prefix / "source/build/plc_main").string();
          std::string cwd = (prefix / "source").string();
          char *args[] = {binary.data(), const_cast<char *>("--safe-mode"),
                          nullptr};
          posix_spawn_file_actions_t actions;
          posix_spawn_file_actions_init(&actions);
          posix_spawn_file_actions_addchdir_np(&actions, cwd.c_str());
          pid_t pid = -1;
          auto result = posix_spawn(&pid, binary.c_str(), &actions, nullptr,
                                    args, environ);
          posix_spawn_file_actions_destroy(&actions);
          check(result == 0, "Native PLC spawn failed");
          child_pid = pid;
          int status;
          while (waitpid(pid, &status, 0) < 0 && errno == EINTR) {
          }
          child_pid = -1;
          logs.append("[ERROR] Native PLC exited; restarting in safe mode");
        } catch (const std::exception &) {
          logs.append("[ERROR] Native PLC startup refused by identity guard");
        }
        std::this_thread::sleep_for(std::chrono::seconds(2));
      }
    }).detach();
  }
  auto handler = [&](const Request &request) -> Response {
    const auto path = target_path(request);
    const auto method = request.method();
    if (ws::is_upgrade(request))
      return response(200, {{"status", "ok"}});
    if (path == "/api/version" && method == http::verb::get)
      return response(200, {{"version", "v4.3.0"}});
    if (path == "/api/capabilities" && method == http::verb::get)
      return response(200, {{"runtimeVersion", "v4.3.0"},
                            {"minEditorVersion", "4.0.0"},
                            {"projectSnapshot", false},
                            {"workbenchNativeManagement", true}});
    if (path == "/api/login" && method == http::verb::post) {
      try {
        return response(
            200, {{"access_token", auth.login(parse_json(request.body()))}});
      } catch (...) {
        return response(401, {{"msg", "Invalid credentials"}});
      }
    }
    if (path == "/api/create-user" && method == http::verb::post) {
      Json caller;
      try {
        caller = auth.authenticate(bearer(request));
      } catch (...) {
      }
      return response(201,
                      auth.create_user(parse_json(request.body()), caller));
    }
    if (path == "/api/get-users-info" && method == http::verb::get) {
      auto users = auth.users();
      try {
        auth.authenticate(bearer(request));
        return response(200, users);
      } catch (...) {
        return response(
            users.empty() ? 404 : 200,
            {{"msg", users.empty() ? "No users found" : "Users found"}});
      }
    }
    std::string token;
    Json user;
    try {
      token = bearer(request);
      user = auth.authenticate(token);
    } catch (...) {
      return response(401, {{"msg", "Unauthorized"}});
    }
    if (path == "/api/logout" && method == http::verb::post) {
      auth.logout(token);
      return response(200, {{"msg", "Successfully logged out"}});
    }
    if (path == "/api/whoami" && method == http::verb::get)
      return response(200, user);
    auto account_id = [&](const std::string &prefix) {
      auto digits = path.substr(prefix.size());
      int64_t id = 0;
      auto result =
          std::from_chars(digits.data(), digits.data() + digits.size(), id);
      check(result.ec == std::errc{} &&
                result.ptr == digits.data() + digits.size() && id > 0,
            "Invalid user ID");
      return id;
    };
    if (path.starts_with("/api/get-user-info/") && method == http::verb::get)
      return response(200, auth.get_user(account_id("/api/get-user-info/")));
    if (path.starts_with("/api/update-user/") && method == http::verb::put) {
      auto input = parse_json(request.body());
      auto id = account_id("/api/update-user/");
      auto result = auth.update_user(id, input, user);
      if (input.contains("password") && user["id"] == id)
        auth.logout(token);
      return response(200, result);
    }
    if (path.starts_with("/api/delete-user/") && method == http::verb::delete_)
      return response(200,
                      auth.delete_user(account_id("/api/delete-user/"), user));
    if (path == "/api/workbench-debug" && method == http::verb::post)
      return response(200, debug_request(ipc, parse_json(request.body())));
    if (method != http::verb::get)
      return response(
          405, {{"msg", "Unsupported operation; use engineering deployment"}});
    if (path == "/api/status") {
      Json result = {{"status", ipc.request("STATUS")}};
      try {
        auto value = ipc.request("SWITCH");
        if (value == "SWITCH:RUN")
          result["switchPosition"] = "run";
        if (value == "SWITCH:STOP")
          result["switchPosition"] = "stop";
        if (request.target().find("include_stats=true") !=
            beast::string_view::npos) {
          auto stats = ipc.request("STATS");
          if (stats.starts_with("STATS:"))
            result["timing_stats"] = parse_json(stats.substr(6));
        }
      } catch (...) {
      }
      return response(200, result);
    }
    if (path == "/api/start-plc")
      return response(200, {{"status", ipc.request("START")}});
    if (path == "/api/stop-plc")
      return response(200, {{"status", ipc.request("STOP")}});
    if (path == "/api/ping")
      return response(200, {{"status", ipc.request("PING")}});
    if (path == "/api/workbench-task-scheduling") {
      auto reply = ipc.request("DEBUG:46 02", std::chrono::seconds(2));
      return response(200, {{"interface", "workbench-task-scheduling-v1"},
                            {"supported", reply == "DEBUG:46 7E 01" ||
                                              reply == "DEBUG:46 7e 01"},
                            {"nativeReply", reply}});
    }
    if (path == "/api/compilation-status")
      return response(200, {{"status", "IDLE"},
                            {"logs", Json::array()},
                            {"exit_code", nullptr}});
    if (path == "/api/runtime-logs") {
      uint64_t id = 0;
      std::string level;
      auto target = std::string(request.target());
      auto at = target.find("id=");
      if (at != target.npos) {
        auto str = target.substr(at + 3);
        auto end = str.find('&');
        if (end != str.npos)
          str.resize(end);
        auto parsed = std::from_chars(str.data(), str.data() + str.size(), id);
        check(parsed.ec == std::errc{}, "Invalid log id");
      }
      at = target.find("level=");
      if (at != target.npos) {
        level = target.substr(at + 6);
        auto end = level.find('&');
        if (end != level.npos)
          level.resize(end);
      }
      return response(200, {{"runtime-logs", logs.get(id, level)}});
    }
    return response(404, {{"msg", "Unknown Runtime route"}});
  };
  WebDebug debug{[&](const std::string &token, bool ignore) {
                   auth.authenticate(token, ignore);
                 },
                 [&](const std::string &, const Json &data) {
                   return debug_request(ipc, data);
                 }};
  read_file(prefix / "source/webserver/keyOPENPLC.pem", 16384, true);
  serve_https(
      port, prefix / "source/webserver/certOPENPLC.pem",
      prefix / "source/webserver/keyOPENPLC.pem",
      [&](const Request &req) {
        try {
          return handler(req);
        } catch (const ApiError &e) {
          return response(e.status, {{"msg", e.what()}});
        } catch (const std::exception &) {
          return response(
              400, {{"msg", "Invalid request or operation unconfirmed"}});
        }
      },
      debug);
  return 0;
}
} // namespace stworkbench::runtime
#ifndef ST_RUNTIME_HOST_NO_MAIN
int main(int argc, char **argv) {
  try {
    return stworkbench::runtime::host_main(argc, argv);
  } catch (const std::exception &e) {
    std::cerr << "Runtime host failed: " << e.what() << '\n';
    return 1;
  }
}
#endif

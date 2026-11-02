// SPDX-License-Identifier: MIT
#include "runtime_http.hpp"
#include <iostream>
#include <regex>
#include <sys/stat.h>
namespace stworkbench::runtime {
inline void validate_deploy(const Json &value) {
  static const std::set<std::string> actions = {"status",
                                                "realtime-status",
                                                "stage",
                                                "compile",
                                                "activate",
                                                "recover",
                                                "start",
                                                "stop",
                                                "configuration-plan",
                                                "configuration-apply",
                                                "hardware-validate",
                                                "hardware-apply",
                                                "hardware-stage",
                                                "program-export",
                                                "hardware-export",
                                                "program-debug-map"};
  static const std::set<std::string> fields = {"action",
                                               "request_id",
                                               "build_hash",
                                               "package_base64",
                                               "package_sha256",
                                               "expected_active_build_hash",
                                               "expected_hardware_hash",
                                               "configuration_identity"};
  check(value.is_object() && value.value("action", Json{}).is_string() &&
            actions.contains(value["action"].get<std::string>()),
        "Unsupported deployment request");
  for (auto it = value.begin(); it != value.end(); ++it)
    check(fields.contains(it.key()), "Unsupported deployment field");
  check(value.value("request_id", Json{}).is_string() &&
            std::regex_match(value["request_id"].get<std::string>(),
                             std::regex("[A-Za-z0-9_-]{1,80}")),
        "Invalid request ID");
  for (auto key : {"build_hash", "package_sha256", "expected_active_build_hash",
                   "expected_hardware_hash", "configuration_identity"})
    if (value.contains(key) && !value[key].is_null())
      check(value[key].is_string() &&
                std::regex_match(value[key].get<std::string>(),
                                 std::regex("[a-fA-F0-9]{64}")),
            "Invalid identity");
  if (value.contains("package_base64"))
    check((value["action"] == "stage" || value["action"] == "hardware-stage") &&
              value["package_base64"].is_string() &&
              value["package_base64"].get_ref<const std::string &>().size() <=
                  32 * 1024 * 1024 * 4 / 3 + 4,
          "Invalid package");
}
class Gateway {
  std::filesystem::path prefix_;
  std::string pairing_;
  std::mutex mutex_;
  std::set<std::string> active_;
  Response invoke(const Json &value) {
    validate_deploy(value);
    std::string id = value["request_id"];
    {
      std::lock_guard lock(mutex_);
      if (!active_.insert(id).second)
        return response(409, {{"error", "Request already in flight"}});
    }
    struct Guard {
      Gateway &g;
      std::string id;
      ~Guard() {
        std::lock_guard lock(g.mutex_);
        g.active_.erase(id);
      }
    } guard{*this, id};
    auto process = run_process(
        {"/home/a/st-workbench-deployment/st-deployment-coordinator"}, {},
        std::chrono::seconds(600), 64 * 1024 * 1024, false, value.dump());
    check(!process.timed_out && !process.truncated,
          "Coordinator result unconfirmed; no retry performed");
    auto result = parse_json(process.output);
    check(result.is_object() && result.value("request_id", Json{}) == id,
          "Coordinator response mismatch");
    check(result.value("success",Json{})!=true||process.exit_code==0,"Coordinator success exit status differs");
    if (result.value("success", Json{}) != true) {
      atomic_write(prefix_ / "engineering/last-failure.json",
                   Json{{"action", value["action"]},
                        {"request_id", id},
                        {"detail", result.value("message", Json{})},
                        {"utc", std::time(nullptr)}}
                       .dump());
      result["message"] =
          "操作失败或结果尚未确认，请查询实际状态；诊断号：" + id;
    }
    return response(200, result);
  }

public:
  explicit Gateway(std::filesystem::path prefix)
      : prefix_(std::move(prefix)),
        pairing_(
            read_file(prefix_ / "engineering/pairing-token", 16384, true)) {
    while (!pairing_.empty() &&
           std::isspace(static_cast<unsigned char>(pairing_.back())))
      pairing_.pop_back();
    check(!pairing_.empty(), "Pairing token missing");
  }
  Response handle(const Request &request) {
    const auto path = target_path(request);
    auto token = std::string(request["X-Engineering-Token"]);
    if (token.empty() && path.starts_with("/engineering/")) {
      try {
        token = bearer(request);
      } catch (...) {
      }
    }
    if (!constant_equal(token, pairing_))
      return response(401, {{"error", "Unauthorized"}});
    if (ws::is_upgrade(request))
      return response(200, {{"status", "ok"}});
    if (path == "/engineering/health" && request.method() == http::verb::get)
      return response(200, {{"success", true}, {"service", "engineering"}});
    if (path == "/engineering/identity" &&
        request.method() == http::verb::get) {
      std::string id = random_id();
      auto target = std::string(request.target());
      auto at = target.find("request_id=");
      if (at != target.npos) {
        id = target.substr(at + 11);
        auto end = id.find('&');
        if (end != id.npos)
          id.resize(end);
      }
      return invoke({{"action", "status"}, {"request_id", id}});
    }
    if (path == "/engineering/deploy" && request.method() == http::verb::post) {
      try {
        auto value = parse_json(request.body());
        validate_deploy(value);
        return invoke(value);
      } catch (const std::invalid_argument &) {
        return response(400, {{"error", "Invalid deployment request"}});
      }
    }
    const auto ca = prefix_ / "source/webserver/certOPENPLC.pem";
    if (path == "/engineering/session" &&
        request.method() == http::verb::post) {
      auto credentials = parse_json(
          read_file(prefix_ / "data/coordinator-auth.json", 16384, true));
      check(credentials.is_object() && credentials.size() == 2 &&
                credentials.value("username", Json{}).is_string() &&
                credentials.value("password", Json{}).is_string(),
            "Invalid private credentials");
      Request upstream{http::verb::post, "/api/login", 11};
      upstream.set(http::field::host, "localhost:8443");
      upstream.set(http::field::content_type, "application/json");
      upstream.body() = credentials.dump();
      upstream.prepare_payload();
      auto result = https_request(8443, ca, upstream);
      check(result.result_int() == 200, "Runtime login failed");
      auto value = parse_json(result.body());
      check(value.value("access_token", Json{}).is_string() &&
                value["access_token"].get_ref<const std::string &>().size() <=
                    12000,
            "Invalid Runtime session");
      return response(200, {{"access_token", value["access_token"]}});
    }
    if (path.starts_with("/api/")) {
      Request upstream{request.method(), request.target(), 11};
      upstream.set(http::field::host, "localhost:8443");
      for (auto name : {http::field::authorization, http::field::content_type,
                        http::field::accept})
        if (request.count(name))
          upstream.set(name, request[name]);
      upstream.body() = request.body();
      upstream.prepare_payload();
      auto result = https_request(8443, ca, upstream);
      if (result.result_int() >= 500)
        return response(502, {{"error", "Runtime unavailable"}});
      Response out{result.result(), 11};
      if (result.count(http::field::content_type))
        out.set(http::field::content_type, result[http::field::content_type]);
      out.set(http::field::cache_control, "no-store");
      out.body() = std::move(result.body());
      out.prepare_payload();
      return out;
    }
    return response(404, {{"error", "Unknown engineering route"}});
  }
};
int gateway_main(int argc, char **argv) {
  std::filesystem::path prefix =
      "/home/a/.local/share/st-workbench/runtime-v4.3.0";
  unsigned port = 18445;
  for (int i = 1; i < argc; i++) {
    std::string arg = argv[i];
    if (arg == "--port" && i + 1 < argc) {
      port = std::stoul(argv[++i]);
      check(port > 0 && port <= 65535, "Invalid port");
    } else
      throw std::runtime_error("Unknown gateway option");
  }
  check(geteuid() != 0, "Engineering gateway must run as ordinary user");
  ::umask(0077);
  read_file(prefix / "engineering/key.pem", 16384, true);
  Gateway gateway(prefix);
  serve_https(
      port, prefix / "engineering/cert.pem", prefix / "engineering/key.pem",
      [&](const Request &req) {
        try {
          return gateway.handle(req);
        } catch (...) {
          return response(502, {{"error", "Backend unavailable or operation "
                                          "unconfirmed; no retry performed."}});
        }
      },
      WebDebug{}, prefix / "source/webserver/certOPENPLC.pem");
  return 0;
}
} // namespace stworkbench::runtime
#ifndef ST_ENGINEERING_GATEWAY_NO_MAIN
int main(int argc, char **argv) {
  try {
    return stworkbench::runtime::gateway_main(argc, argv);
  } catch (const std::exception &e) {
    std::cerr << "Engineering gateway failed: " << e.what() << '\n';
    return 1;
  }
}
#endif

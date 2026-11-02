// SPDX-License-Identifier: MIT
#include "../src/runtime_http.hpp"
#include <cassert>
#include <iostream>
using namespace stworkbench;
using namespace stworkbench::runtime;
int main(int argc, char **argv) {
  check(argc == 3, "Expected runtime/gateway and port");
  bool gateway = std::string(argv[1]) == "gateway";
  auto port = std::stoul(argv[2]);
  std::filesystem::path prefix =
      "/home/a/.local/share/st-workbench/runtime-v4.3.0";
  auto ca = prefix / (gateway ? "engineering/cert.pem"
                              : "source/webserver/certOPENPLC.pem");
  std::string pairing;
  if (gateway) {
    pairing = read_file(prefix / "engineering/pairing-token", 16384, true);
    while (!pairing.empty() &&
           std::isspace(static_cast<unsigned char>(pairing.back())))
      pairing.pop_back();
  }
  auto call = [&](http::verb method, const std::string &path,
                  const Json &body = Json{},
                  const std::string &token = std::string()) {
    Request request{method, path, 11};
    request.set(http::field::host, "localhost");
    if (gateway)
      request.set("X-Engineering-Token", pairing);
    if (!token.empty())
      request.set(http::field::authorization, "Bearer " + token);
    if (!body.is_null()) {
      request.set(http::field::content_type, "application/json");
      request.body() = body.dump();
    }
    request.prepare_payload();
    return https_request(port, ca, request);
  };
  auto version = call(http::verb::get, "/api/version");
  assert(version.result_int() == 200);
  assert(parse_json(version.body())["version"].is_string());
  auto noauth = call(http::verb::get, "/api/whoami");
  assert(noauth.result_int() == 401);
  auto credentials =
      parse_json(read_file(prefix / "data/coordinator-auth.json", 16384, true));
  auto login = call(http::verb::post, "/api/login", credentials);
  assert(login.result_int() == 200);
  auto token = parse_json(login.body()).at("access_token").get<std::string>();
  auto who = call(http::verb::get, "/api/whoami", nullptr, token);
  assert(who.result_int() == 200);
  assert(parse_json(who.body())["username"] == credentials["username"]);
  net::io_context io;
  ssl::context context(ssl::context::tls_client);
  context.load_verify_file(ca.string());
  context.set_verify_mode(ssl::verify_peer);
  ws::stream<beast::ssl_stream<beast::tcp_stream>> socket(io, context);
  SSL_set_tlsext_host_name(socket.next_layer().native_handle(), "localhost");
  socket.next_layer().set_verify_callback(
      ssl::host_name_verification("localhost"));
  beast::get_lowest_layer(socket).connect(
      {net::ip::make_address("127.0.0.1"), static_cast<unsigned short>(port)});
  socket.next_layer().handshake(ssl::stream_base::client);
  if (gateway)
    socket.set_option(
        ws::stream_base::decorator([&](ws::request_type &request) {
          request.set("X-Engineering-Token", pairing);
        }));
  socket.handshake("localhost", "/socket.io/?EIO=4&transport=websocket");
  beast::flat_buffer buffer;
  auto read = [&] {
    socket.read(buffer);
    auto s = beast::buffers_to_string(buffer.data());
    buffer.consume(buffer.size());
    return s;
  };
  assert(read().starts_with("0{"));
  socket.write(net::buffer("40/api/debug," + Json{{"token", token}}.dump()));
  const auto first = read(), second = read();
  assert((first.starts_with("40/api/debug,") &&
          second.starts_with("42/api/debug,")) ||
         (second.starts_with("40/api/debug,") &&
          first.starts_with("42/api/debug,")));
  socket.write(net::buffer("42/api/debug," +
                           Json::array({"reauth", {{"token", token}}}).dump()));
  assert(read().find("reauth_result") != std::string::npos);
  boost::system::error_code ec;
  beast::get_lowest_layer(socket).socket().close(ec);
  auto logout = call(http::verb::post, "/api/logout", Json::object(), token);
  assert(logout.result_int() == 200);
  assert(call(http::verb::get, "/api/whoami", nullptr, token).result_int() ==
         401);
  std::cout << (gateway ? "gateway" : "runtime")
            << " TLS login, identity, Socket.IO connect/reauth and revocation "
               "passed\n";
}

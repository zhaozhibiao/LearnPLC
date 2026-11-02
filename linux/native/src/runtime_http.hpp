// SPDX-License-Identifier: MIT
#pragma once
#include "runtime_auth.hpp"
#include <atomic>
#include <boost/asio.hpp>
#include <boost/asio/ssl.hpp>
#include <boost/beast.hpp>
#include <boost/beast/ssl.hpp>
#include <boost/beast/websocket.hpp>
#include <deque>
#include <thread>
namespace stworkbench::runtime {
namespace net = boost::asio;
namespace beast = boost::beast;
namespace http = beast::http;
namespace ws = beast::websocket;
namespace ssl = net::ssl;
using tcp = net::ip::tcp;
using Request = http::request<http::string_body>;
using Response = http::response<http::string_body>;
inline Response response(unsigned status, const Json &body,
                         unsigned version = 11) {
  Response out{static_cast<http::status>(status), version};
  out.set(http::field::content_type, "application/json; charset=utf-8");
  out.set(http::field::cache_control, "no-store");
  out.set("X-Content-Type-Options", "nosniff");
  out.keep_alive(false);
  out.body() = body.dump();
  out.prepare_payload();
  return out;
}
inline std::string bearer(const Request &req) {
  auto h = req[http::field::authorization];
  check(h.size() >= 7 && h.substr(0, 7) == "Bearer ", "Unauthorized");
  return std::string(h.substr(7));
}
inline std::string target_path(const Request &r) {
  auto t = r.target();
  return std::string(t.substr(0, t.find('?')));
}
struct WebDebug {
  std::function<void(const std::string &, bool)> authenticate;
  std::function<Json(const std::string &, const Json &)> command;
};
using HttpHandler = std::function<Response(const Request &)>;
class DebugSession : public std::enable_shared_from_this<DebugSession> {
  ws::stream<beast::ssl_stream<beast::tcp_stream>> socket_;
  net::steady_timer timer_;
  beast::flat_buffer buffer_;
  std::deque<std::string> writes_;
  WebDebug handlers_;
  std::string token_;
  bool connected_ = false, closed_ = false;
  std::chrono::steady_clock::time_point pong_ =
      std::chrono::steady_clock::now();
  void close() {
    if (closed_)
      return;
    closed_ = true;
    timer_.cancel();
    boost::system::error_code e;
    beast::get_lowest_layer(socket_).socket().cancel(e);
    beast::get_lowest_layer(socket_).socket().close(e);
  }
  void send(std::string s) {
    if (closed_)
      return;
    check(writes_.size() < 16, "Debug outgoing queue exceeded");
    bool idle = writes_.empty();
    writes_.push_back(std::move(s));
    if (idle)
      write();
  }
  void write() {
    socket_.text(true);
    socket_.async_write(net::buffer(writes_.front()),
                        [self = shared_from_this()](auto e, auto) {
                          if (e)
                            return self->close();
                          self->writes_.pop_front();
                          if (!self->writes_.empty())
                            self->write();
                        });
  }
  void event(const std::string &kind, const Json &value) {
    send("42/api/debug," + Json::array({kind, value}).dump());
  }
  void heartbeat() {
    timer_.expires_after(std::chrono::seconds(20));
    timer_.async_wait([self = shared_from_this()](auto e) {
      if (e)
        return;
      if (std::chrono::steady_clock::now() - self->pong_ >
          std::chrono::seconds(45))
        return self->close();
      self->send("2");
      self->heartbeat();
    });
  }
  void packet(const std::string &s) {
    if (s == "3") {
      pong_ = std::chrono::steady_clock::now();
      return;
    }
    if (s == "2") {
      send("3");
      return;
    }
    if (s.starts_with("40/api/debug,")) {
      try {
        auto auth = parse_json(s.substr(13));
        check(auth.value("token", Json{}).is_string(), "Unauthorized");
        auto token = auth["token"].get<std::string>();
        handlers_.authenticate(token, false);
        token_ = std::move(token);
        connected_ = true;
        send("40/api/debug," + Json{{"sid", random_id()}}.dump());
        event("connected", {{"status", "ok"}});
      } catch (...) {
        send("44/api/debug,{\"message\":\"Unauthorized\"}");
      }
      return;
    }
    if (s == "41/api/debug,") {
      connected_ = false;
      token_.clear();
      return;
    }
    if (!s.starts_with("42/api/debug,"))
      return;
    auto data = parse_json(s.substr(13));
    check(data.is_array() && data.size() == 2 && data[0].is_string() &&
              data[1].is_object(),
          "Invalid debug event");
    std::string kind = data[0];
    if (kind == "reauth") {
      try {
        auto token = data[1].at("token").get<std::string>();
        handlers_.authenticate(token, false);
        token_ = std::move(token);
        connected_ = true;
        event("reauth_result", {{"success", true}});
      } catch (...) {
        event("reauth_result", {{"success", false}, {"error", "Unauthorized"}});
      }
      return;
    }
    if (kind == "debug_command") {
      try {
        check(connected_, "Unauthorized");
        handlers_.authenticate(token_, true);
        event("debug_response", handlers_.command(token_, data[1]));
      } catch (...) {
        event("debug_response",
              {{"success", false},
               {"error", "Debug request failed or unauthorized"}});
      }
    }
  }
  void read() {
    socket_.async_read(buffer_, [self = shared_from_this()](auto e, auto) {
      if (e)
        return self->close();
      try {
        check(self->socket_.got_text(), "Text packet required");
        auto s = beast::buffers_to_string(self->buffer_.data());
        self->buffer_.consume(self->buffer_.size());
        self->packet(s);
        self->read();
      } catch (...) {
        self->close();
      }
    });
  }

public:
  DebugSession(beast::ssl_stream<beast::tcp_stream> stream, WebDebug handlers)
      : socket_(std::move(stream)), timer_(socket_.get_executor()),
        handlers_(std::move(handlers)) {}
  void run(const Request &req) {
    socket_.read_message_max(1024 * 1024);
    socket_.set_option(
        ws::stream_base::timeout::suggested(beast::role_type::server));
    socket_.async_accept(req, [self = shared_from_this()](auto e) {
      if (e)
        return self->close();
      self->send("0" + Json{{"sid", random_id()},
                            {"upgrades", Json::array()},
                            {"pingInterval", 20000},
                            {"pingTimeout", 25000},
                            {"maxPayload", 1024 * 1024}}
                           .dump());
      self->heartbeat();
      self->read();
    });
  }
};
class WebsocketProxy : public std::enable_shared_from_this<WebsocketProxy> {
  inline static std::atomic<unsigned> count_=0;
  ssl::context context_{ssl::context::tls_client};
  ws::stream<beast::ssl_stream<beast::tcp_stream>> front_, back_;
  beast::flat_buffer from_front_, from_back_;
  Request request_;
  bool closed_ = false;
  void close() {
    if (closed_)
      return;
    closed_ = true;
    boost::system::error_code e;
    for (auto socket : {&beast::get_lowest_layer(front_).socket(),
                        &beast::get_lowest_layer(back_).socket()}) {
      socket->cancel(e);
      socket->close(e);
    }
  }
  void read_front() {
    front_.async_read(from_front_, [self = shared_from_this()](auto e, auto) {
      if (e)
        return self->close();
      self->back_.text(self->front_.got_text());
      self->back_.async_write(self->from_front_.data(), [self](auto e, auto) {
        if (e)
          return self->close();
        self->from_front_.consume(self->from_front_.size());
        self->read_front();
      });
    });
  }
  void read_back() {
    back_.async_read(from_back_, [self = shared_from_this()](auto e, auto) {
      if (e)
        return self->close();
      self->front_.text(self->back_.got_text());
      self->front_.async_write(self->from_back_.data(), [self](auto e, auto) {
        if (e)
          return self->close();
        self->from_back_.consume(self->from_back_.size());
        self->read_back();
      });
    });
  }

public:
  WebsocketProxy(beast::ssl_stream<beast::tcp_stream> stream,
                 const std::filesystem::path &ca, Request request)
      : front_(std::move(stream)), back_(front_.get_executor(), context_),
        request_(std::move(request)) {
    context_.load_verify_file(ca.string());
    context_.set_verify_mode(ssl::verify_peer);
    back_.next_layer().set_verify_mode(ssl::verify_peer);
    check(SSL_set_tlsext_host_name(back_.next_layer().native_handle(),
                                   "localhost") == 1,
          "TLS hostname failed");
    back_.next_layer().set_verify_callback(
        ssl::host_name_verification("localhost"));
    front_.read_message_max(1024 * 1024);
    back_.read_message_max(1024 * 1024);
    if(count_.fetch_add(1)>=8){count_.fetch_sub(1);throw std::runtime_error("WebSocket proxy capacity exceeded");}
  }
  ~WebsocketProxy(){--count_;}
  void run() {
    beast::get_lowest_layer(back_).expires_after(std::chrono::seconds(15));
    beast::get_lowest_layer(back_).async_connect(
        tcp::endpoint(net::ip::make_address("127.0.0.1"), 8443),
        [self = shared_from_this()](auto e) {
          if (e)
            return self->close();
          self->back_.next_layer().async_handshake(
              ssl::stream_base::client, [self](auto e) {
                if (e)
                  return self->close();
                self->back_.async_handshake(
                    "localhost:8443", std::string(self->request_.target()),
                    [self](auto e) {
                      if (e)
                        return self->close();
                      beast::get_lowest_layer(self->back_).expires_never();
                      self->front_.set_option(
                          ws::stream_base::timeout::suggested(
                              beast::role_type::server));
                      self->back_.set_option(
                          ws::stream_base::timeout::suggested(
                              beast::role_type::client));
                      self->front_.async_accept(self->request_, [self](auto e) {
                        if (e)
                          return self->close();
                        self->read_front();
                        self->read_back();
                      });
                    });
              });
        });
  }
};
class HttpSession : public std::enable_shared_from_this<HttpSession> {
  beast::ssl_stream<beast::tcp_stream> stream_;
  beast::flat_buffer buffer_;
  http::request_parser<http::string_body> parser_;
  HttpHandler handler_;
  WebDebug debug_;
  std::filesystem::path proxy_ca_;
  std::shared_ptr<Response> reply_;
  void fail() {
    boost::system::error_code e;
    beast::get_lowest_layer(stream_).socket().close(e);
  }
  void read() {
    parser_.body_limit(48 * 1024 * 1024);
    parser_.header_limit(32768);
    beast::get_lowest_layer(stream_).expires_after(std::chrono::seconds(20));
    http::async_read(
        stream_, buffer_, parser_, [self = shared_from_this()](auto e, auto) {
          if (e)
            return self->fail();
          auto req = self->parser_.release();
          if (ws::is_upgrade(req) && target_path(req) == "/socket.io/") {
            try {
              auto admission = self->handler_(req);
              check(admission.result_int() == 200,
                    "WebSocket authorization rejected");
              if (!self->proxy_ca_.empty())
                std::make_shared<WebsocketProxy>(std::move(self->stream_),
                                                 self->proxy_ca_, req)
                    ->run();
              else
                std::make_shared<DebugSession>(std::move(self->stream_),
                                               self->debug_)
                    ->run(req);
            } catch (...) {
              self->fail();
            }
            return;
          }
          try {
            self->reply_ = std::make_shared<Response>(self->handler_(req));
          } catch (...) {
            self->reply_ = std::make_shared<Response>(response(
                500, {{"msg", "Request failed or state unconfirmed"}}));
          }
          beast::get_lowest_layer(self->stream_)
              .expires_after(std::chrono::seconds(20));
          http::async_write(self->stream_, *self->reply_,
                            [self](auto, auto) { self->fail(); });
        });
  }

public:
  HttpSession(tcp::socket s, ssl::context &ctx, HttpHandler h, WebDebug d,
              std::filesystem::path proxy_ca)
      : stream_(std::move(s), ctx), handler_(std::move(h)),
        debug_(std::move(d)), proxy_ca_(std::move(proxy_ca)) {}
  void run() {
    beast::get_lowest_layer(stream_).expires_after(std::chrono::seconds(10));
    stream_.async_handshake(ssl::stream_base::server,
                            [self = shared_from_this()](auto e) {
                              if (e)
                                return self->fail();
                              self->read();
                            });
  }
};
inline void serve_https(unsigned short port, const std::filesystem::path &cert,
                        const std::filesystem::path &key, HttpHandler handler,
                        WebDebug debug, std::filesystem::path proxy_ca = {}) {
  ssl::context ctx(ssl::context::tls_server);
  ctx.set_options(ssl::context::no_sslv2 | ssl::context::no_sslv3 |
                  ssl::context::no_tlsv1 | ssl::context::no_tlsv1_1);
  ctx.use_certificate_chain_file(cert.string());
  ctx.use_private_key_file(key.string(), ssl::context::pem);
  net::io_context accept_io;
  tcp::acceptor acceptor(accept_io, {tcp::v4(), port});
  std::atomic<unsigned> connections = 0;
  for (;;) {
    tcp::socket incoming(accept_io);
    acceptor.accept(incoming);
    if (connections >= 16) {
      boost::system::error_code e;
      incoming.close(e);
      continue;
    }
    int fd = incoming.release();
    ++connections;
    std::thread([&, fd, handler, debug, proxy_ca] {
      try {
        Fd owned(fd);
        net::io_context io;
        tcp::socket socket(io);
        socket.assign(tcp::v4(), fd);
        owned.release();
        std::make_shared<HttpSession>(std::move(socket), ctx, handler, debug,
                                      proxy_ca)
            ->run();
        io.run();
      } catch (...) {
      }
      --connections;
    }).detach();
  }
}
inline Response https_request(unsigned short port,
                              const std::filesystem::path &ca,
                              const Request &input) {
  net::io_context io;
  ssl::context ctx(ssl::context::tls_client);
  ctx.load_verify_file(ca.string());
  ctx.set_verify_mode(ssl::verify_peer);
  beast::ssl_stream<beast::tcp_stream> stream(io, ctx);
  check(SSL_set_tlsext_host_name(stream.native_handle(), "localhost") == 1,
        "TLS hostname failed");
  stream.set_verify_callback(ssl::host_name_verification("localhost"));
  std::optional<Response> result;
  std::exception_ptr failure;
  beast::flat_buffer buffer;
  http::response_parser<http::string_body> parser;
  parser.body_limit(64 * 1024 * 1024);
  beast::get_lowest_layer(stream).expires_after(std::chrono::seconds(30));
  beast::get_lowest_layer(stream).async_connect(
      tcp::endpoint(net::ip::make_address("127.0.0.1"), port), [&](auto e) {
        if (e) {
          failure = std::make_exception_ptr(
              std::runtime_error("Runtime HTTPS unavailable"));
          return;
        }
        stream.async_handshake(ssl::stream_base::client, [&](auto e) {
          if (e) {
            failure = std::make_exception_ptr(
                std::runtime_error("Runtime TLS rejected"));
            return;
          }
          http::async_write(stream, input, [&](auto e, auto) {
            if (e) {
              failure = std::make_exception_ptr(
                  std::runtime_error("Runtime request unconfirmed"));
              return;
            }
            http::async_read(stream, buffer, parser, [&](auto e, auto) {
              if (e)
                failure = std::make_exception_ptr(
                    std::runtime_error("Runtime response unconfirmed"));
              else
                result = parser.release();
            });
          });
        });
      });
  io.run();
  boost::system::error_code e;
  beast::get_lowest_layer(stream).socket().close(e);
  if (failure)
    std::rethrow_exception(failure);
  check(result.has_value(), "Runtime request failed");
  return std::move(*result);
}
} // namespace stworkbench::runtime

// SPDX-License-Identifier: MIT
#include "stworkbench/common.hpp"
#include <arpa/inet.h>
#include <iostream>
#include <memory>
#include <openssl/pem.h>
#include <openssl/x509.h>
#include <pwd.h>
#include <set>
namespace stworkbench::session {
inline constexpr char Prefix[] =
    "/home/a/.local/share/st-workbench/runtime-v4.3.0";
void require(bool good, const char *message) {
  if (!good)
    throw std::runtime_error(message);
}
std::string endpoint(const std::string &address) {
  in6_addr buffer{};
  if (::inet_pton(AF_INET, address.c_str(), &buffer) == 1)
    return "https://" + address + ":18445";
  require(::inet_pton(AF_INET6, address.c_str(), &buffer) == 1,
          "IP address required");
  return "https://[" + address + "]:18445";
}
void validate_credentials(const Json &auth) {
  require(auth.is_object() && auth.size() == 2 && auth.contains("username") &&
              auth.contains("password"),
          "Invalid private credentials");
  for (const auto &value : auth) {
    require(value.is_string(), "Invalid private credentials");
    const auto text = value.get<std::string>();
    require(!text.empty() && text.size() <= 4096 &&
                text.find('\0') == std::string::npos,
            "Invalid private credentials");
  }
}
void validate_token(const std::string &token) {
  require(!token.empty() && token.size() <= 12000 &&
              token.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMN"
                                      "OPQRSTUVWXYZ0123456789-._~+/=") ==
                  std::string::npos,
          "Invalid private session token");
}
Json pairing(const std::filesystem::path &root, const std::string &address) {
  const auto origin = endpoint(address);
  auto token = read_file(root / "pairing-token", 16384, true);
  const auto first = token.find_first_not_of(" \r\n\t");
  const auto last = token.find_last_not_of(" \r\n\t");
  require(first != std::string::npos, "Pairing token unavailable");
  token = token.substr(first, last - first + 1);
  require(token.size() >= 32 && token.size() <= 8192, "Pairing token invalid");
  for (const unsigned char c : token)
    require(c >= 33 && c <= 126, "Pairing token invalid");
  auto pem = read_file(root / "cert.pem", 65536);
  std::unique_ptr<BIO, decltype(&BIO_free)> bio(
      BIO_new_mem_buf(pem.data(), static_cast<int>(pem.size())), BIO_free);
  require(bool(bio), "Certificate unavailable");
  std::unique_ptr<X509, decltype(&X509_free)> cert(
      PEM_read_bio_X509(bio.get(), nullptr, nullptr, nullptr), X509_free);
  require(bool(cert), "Certificate invalid");
  const auto size = i2d_X509(cert.get(), nullptr);
  require(size > 0 && size <= 65536, "Certificate invalid");
  std::string der(static_cast<std::size_t>(size), '\0');
  auto p = reinterpret_cast<unsigned char *>(der.data());
  require(i2d_X509(cert.get(), &p) == size, "Certificate encoding failed");
  return {{"endpoint", origin},
          {"token", token},
          {"certificate_sha256", sha256(der)}};
}
Json runtime_session(const std::filesystem::path &root) {
  const auto auth =
      parse_json(read_file(root / "data/coordinator-auth.json", 16384, true));
  validate_credentials(auth);
  // curl is a native executable. Secrets travel through the private stdin pipe.
  auto response =
      run_process({"/usr/bin/curl",
                   "-q",
                   "--silent",
                   "--show-error",
                   "--fail",
                   "--proto",
                   "=https",
                   "--noproxy",
                   "*",
                   "--max-redirs",
                   "0",
                   "--max-time",
                   "10",
                   "--cacert",
                   (root / "source/webserver/certOPENPLC.pem").string(),
                   "--header",
                   "Content-Type: application/json",
                   "--data-binary",
                   "@-",
                   "https://localhost:8443/api/login"},
                  {}, std::chrono::seconds(12), 16384, true, auth.dump());
  require(!response.timed_out && !response.truncated && response.exit_code == 0,
          "Private session authentication unavailable");
  const auto value = parse_json(response.output);
  require(value.is_object() && value.contains("access_token") &&
              value["access_token"].is_string(),
          "Private session authentication unavailable");
  const auto token = value["access_token"].get<std::string>();
  validate_token(token);
  return {{"access_token", token}};
}
} // namespace stworkbench::session
#ifndef ST_SESSION_HELPER_NO_MAIN
int main(int argc, char **argv) {
  try {
    const auto account = ::getpwuid(::getuid());
    stworkbench::session::require(account &&
                                      std::string(account->pw_dir) == "/home/a",
                                  "Unexpected session user");
    stworkbench::Json value;
    if (argc == 2 && std::string_view(argv[1]) == "--runtime-session")
      value =
          stworkbench::session::runtime_session(stworkbench::session::Prefix);
    else if (argc == 3 && std::string_view(argv[1]) == "--pair")
      value = stworkbench::session::pairing(
          std::filesystem::path(stworkbench::session::Prefix) / "engineering",
          argv[2]);
    else
      throw std::runtime_error("Invalid session helper invocation");
    // This output contains a secret and is consumed only by the SSH caller.
    std::cout << value.dump() << '\n';
    return 0;
  } catch (...) {
    std::cout << "{\"error\":\"Private session authentication unavailable\"}\n";
    return 1;
  }
}
#endif

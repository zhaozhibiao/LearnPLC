// SPDX-License-Identifier: MIT
// Compatible with the pinned Autonomy Runtime MIT authentication schema.
#pragma once
#include "stworkbench/common.hpp"
#include <charconv>
#include <ctime>
#include <map>
#include <mutex>
#include <openssl/crypto.h>
#include <openssl/evp.h>
#include <openssl/hmac.h>
#include <openssl/rand.h>
#include <set>
#include <sqlite3.h>
#include <sstream>
#include <sys/stat.h>
namespace stworkbench::runtime {
struct ApiError : std::runtime_error {
  unsigned status;
  ApiError(unsigned s, const char *message)
      : std::runtime_error(message), status(s) {}
};
inline void check(bool ok, const char *message) {
  if (!ok)
    throw std::runtime_error(message);
}
inline std::string b64url(std::string_view bytes) {
  std::string out(4 * ((bytes.size() + 2) / 3), '\0');
  auto n =
      EVP_EncodeBlock(reinterpret_cast<unsigned char *>(out.data()),
                      reinterpret_cast<const unsigned char *>(bytes.data()),
                      static_cast<int>(bytes.size()));
  out.resize(n);
  for (auto &c : out) {
    if (c == '+')
      c = '-';
    else if (c == '/')
      c = '_';
  }
  while (!out.empty() && out.back() == '=')
    out.pop_back();
  return out;
}
inline std::string unbase64(std::string_view input) {
  check(input.size() <= 1024 * 1024, "Invalid base64 capacity");
  std::string s(input);
  for (auto &c : s) {
    if (c == '-')
      c = '+';
    else if (c == '_')
      c = '/';
    else
      check(std::isalnum(static_cast<unsigned char>(c)) || c == '+' ||
                c == '/' || c == '=',
            "Invalid base64");
  }
  while (s.size() % 4)
    s += '=';
  std::string out(s.size() / 4 * 3, '\0');
  const auto n =
      EVP_DecodeBlock(reinterpret_cast<unsigned char *>(out.data()),
                      reinterpret_cast<const unsigned char *>(s.data()),
                      static_cast<int>(s.size()));
  check(n >= 0, "Invalid base64");
  size_t padding = 0;
  for (auto i = s.rbegin(); i != s.rend() && *i == '='; ++i)
    ++padding;
  check(padding <= 2 && static_cast<size_t>(n) >= padding,
        "Invalid base64 padding");
  out.resize(n - padding);
  return out;
}
inline std::string hex(std::string_view bytes) {
  constexpr char digits[] = "0123456789abcdef";
  std::string out;
  out.reserve(bytes.size() * 2);
  for (unsigned char c : bytes) {
    out += digits[c >> 4];
    out += digits[c & 15];
  }
  return out;
}
inline std::string random_id() {
  unsigned char bytes[24];
  check(RAND_bytes(bytes, sizeof bytes) == 1, "Random source failed");
  return hex({reinterpret_cast<char *>(bytes), sizeof bytes});
}
inline bool constant_equal(std::string_view a, std::string_view b) {
  return a.size() == b.size() &&
         CRYPTO_memcmp(a.data(), b.data(), a.size()) == 0;
}
inline bool password_matches(const std::string &record,
                             const std::string &password) {
  const auto p = record.find('$'),
             q = record.find('$',
                             p == std::string::npos ? record.size() : p + 1);
  if (p == std::string::npos || q == std::string::npos)
    return false;
  auto method = record.substr(0, p), salt = record.substr(p + 1, q - p - 1),
       expected = record.substr(q + 1);
  if (!method.starts_with("pbkdf2:sha256:"))
    return false;
  unsigned rounds = 0;
  auto digits = method.substr(14);
  auto parsed =
      std::from_chars(digits.data(), digits.data() + digits.size(), rounds);
  if (parsed.ec != std::errc{} || parsed.ptr != digits.data() + digits.size() ||
      rounds < 1 || rounds > 2000000 || expected.size() != 64 ||
      salt.size() > 256)
    return false;
  unsigned char digest[32];
  if (PKCS5_PBKDF2_HMAC(password.data(), password.size(),
                        reinterpret_cast<const unsigned char *>(salt.data()),
                        salt.size(), rounds, EVP_sha256(), 32, digest) != 1)
    return false;
  return constant_equal(hex({reinterpret_cast<char *>(digest), sizeof digest}),
                        expected);
}
inline std::string hmac(std::string_view content, std::string_view secret) {
  unsigned char digest[EVP_MAX_MD_SIZE];
  unsigned size = 0;
  check(HMAC(EVP_sha256(), secret.data(), static_cast<int>(secret.size()),
             reinterpret_cast<const unsigned char *>(content.data()),
             content.size(), digest, &size) != nullptr,
        "Token signing failed");
  return {reinterpret_cast<char *>(digest), size};
}
inline std::string sign_token(const Json &payload, const std::string &secret) {
  auto data = b64url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." +
              b64url(payload.dump());
  return data + "." + b64url(hmac(data, secret));
}
inline Json verify_token(const std::string &token, const std::string &secret,
                         bool ignore_expiry = false) {
  check(token.size() <= 16384, "Invalid token capacity");
  auto a = token.find('.'),
       b = token.find('.', a == std::string::npos ? token.size() : a + 1);
  check(a != std::string::npos && b != std::string::npos &&
            token.find('.', b + 1) == std::string::npos,
        "Invalid token");
  check(constant_equal(unbase64(token.substr(b + 1)),
                       hmac(token.substr(0, b), secret)),
        "Unauthorized");
  auto header = parse_json(unbase64(token.substr(0, a))),
       payload = parse_json(unbase64(token.substr(a + 1, b - a - 1)));
  check(header.is_object() && header.value("alg", Json{}) == "HS256" &&
            payload.is_object() && payload.value("type", Json{}) == "access",
        "Unauthorized");
  check(payload.contains("exp") && payload["exp"].is_number_integer(),
        "Invalid token expiry");
  auto now = std::time(nullptr);
  check(ignore_expiry || payload["exp"].get<int64_t>() > now, "Token expired");
  if (payload.contains("nbf"))
    check(payload["nbf"].is_number_integer() &&
              payload["nbf"].get<int64_t>() <= now,
          "Token not active");
  return payload;
}
class Auth {
  std::filesystem::path database_;
  std::string secret_, pepper_;
  std::mutex mutex_;
  std::set<std::string> revoked_;
  struct Database {
    sqlite3 *value = nullptr;
    explicit Database(const std::filesystem::path &p, bool write = false) {
      if (sqlite3_open_v2(p.c_str(), &value,
                          (write ? (SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE)
                                 : SQLITE_OPEN_READONLY) |
                              SQLITE_OPEN_FULLMUTEX,
                          nullptr) != SQLITE_OK) {
        if (value)
          sqlite3_close(value);
        throw std::runtime_error("Authentication database unavailable");
      }
      sqlite3_busy_timeout(value, 500);
    }
    ~Database() { sqlite3_close(value); }
  };
  static void execute(Database &db, const char *sql,
                      const std::vector<Json> &args = {}) {
    sqlite3_stmt *raw = nullptr;
    check(sqlite3_prepare_v2(db.value, sql, -1, &raw, nullptr) == SQLITE_OK,
          "Account operation unavailable");
    std::unique_ptr<sqlite3_stmt, decltype(&sqlite3_finalize)> statement(
        raw, sqlite3_finalize);
    for (size_t i = 0; i < args.size(); i++) {
      if (args[i].is_string()) {
        auto &s = args[i].get_ref<const std::string &>();
        sqlite3_bind_text(raw, i + 1, s.data(), s.size(), SQLITE_TRANSIENT);
      } else
        sqlite3_bind_int64(raw, i + 1, args[i].get<int64_t>());
    }
    if (sqlite3_step(raw) != SQLITE_DONE)
      throw ApiError(409, "Account operation conflicts with existing data");
  }
  std::string hash_password(const std::string &password) {
    check(!password.empty() && password.size() <= 4096, "Invalid password");
    auto salt = random_id().substr(0, 16), input = password + pepper_;
    unsigned char digest[32];
    check(
        PKCS5_PBKDF2_HMAC(input.data(), input.size(),
                          reinterpret_cast<const unsigned char *>(salt.data()),
                          salt.size(), 600000, EVP_sha256(), 32, digest) == 1,
        "Password derivation failed");
    return "pbkdf2:sha256:600000$" + salt + "$" +
           hex({reinterpret_cast<char *>(digest), sizeof digest});
  }
  Json user(const char *sql, const std::string &argument,
            bool password = false) {
    Database db(database_);
    sqlite3_stmt *raw = nullptr;
    check(sqlite3_prepare_v2(db.value, sql, -1, &raw, nullptr) == SQLITE_OK,
          "Authentication query failed");
    std::unique_ptr<sqlite3_stmt, decltype(&sqlite3_finalize)> statement(
        raw, sqlite3_finalize);
    sqlite3_bind_text(raw, 1, argument.data(), argument.size(),
                      SQLITE_TRANSIENT);
    if (sqlite3_step(raw) != SQLITE_ROW)
      return nullptr;
    auto text = [&](int i) {
      auto p = sqlite3_column_text(raw, i);
      return p ? std::string(reinterpret_cast<const char *>(p)) : std::string();
    };
    Json out = {{"id", sqlite3_column_int64(raw, 0)},
                {"username", text(1)},
                {"role", text(2)}};
    if (password)
      out["password_hash"] = text(3);
    return out;
  }

public:
  explicit Auth(const std::filesystem::path &data)
      : database_(data / "restapi.db") {
    if (!std::filesystem::exists(data / ".env")) {
      check(!std::filesystem::exists(database_),
            "Existing authentication database requires original private "
            "configuration");
      atomic_write(data / ".env", "JWT_SECRET_KEY=" + random_id() +
                                      random_id().substr(0, 16) +
                                      "\nPEPPER=" + random_id() +
                                      random_id().substr(0, 16) + "\n");
    }
    auto env = read_file(data / ".env", 16384, true);
    std::istringstream lines(env);
    std::string line;
    while (std::getline(lines, line)) {
      if (!line.empty() && line.back() == '\r')
        line.pop_back();
      auto at = line.find('=');
      if (at == line.npos)
        continue;
      auto key = line.substr(0, at), value = line.substr(at + 1);
      if (key == "JWT_SECRET_KEY")
        secret_ = value;
      if (key == "PEPPER")
        pepper_ = value;
    }
    check(secret_.size() == 64 && pepper_.size() == 64,
          "Invalid private authentication configuration");
    if (!std::filesystem::exists(database_)) {
      Database db(database_, true);
      execute(db, "CREATE TABLE users(id INTEGER PRIMARY KEY,username TEXT NOT "
                  "NULL UNIQUE,password_hash TEXT NOT NULL,role TEXT NOT NULL "
                  "DEFAULT 'admin')");
      ::chmod(database_.c_str(), 0600);
    }else read_file(database_,32*1024*1024);
  }
  Json users() {
    Database db(database_);
    sqlite3_stmt *raw = nullptr;
    check(sqlite3_prepare_v2(db.value,
                             "SELECT id,username,role FROM users ORDER BY id",
                             -1, &raw, nullptr) == SQLITE_OK,
          "Account list unavailable");
    std::unique_ptr<sqlite3_stmt, decltype(&sqlite3_finalize)> statement(
        raw, sqlite3_finalize);
    Json out = Json::array();
    while (sqlite3_step(raw) == SQLITE_ROW) {
      check(out.size() < 10000, "Account capacity exceeded");
      out.push_back(
          {{"id", sqlite3_column_int64(raw, 0)},
           {"username",
            reinterpret_cast<const char *>(sqlite3_column_text(raw, 1))},
           {"role",
            reinterpret_cast<const char *>(sqlite3_column_text(raw, 2))}});
    }
    return out;
  }
  Json get_user(int64_t id) {
    auto value = user("SELECT id,username,role FROM users WHERE id=?",
                      std::to_string(id));
    if (value.is_null())
      throw ApiError(404, "User not found");
    return value;
  }
  Json create_user(const Json &input, const Json &caller) {
    check(input.is_object() && input.value("username", Json{}).is_string() &&
              input.value("password", Json{}).is_string(),
          "Missing username or password");
    auto name = input["username"].get<std::string>(),
         password = input["password"].get<std::string>();
    check(!name.empty() && name.size() <= 4096, "Invalid username");
    auto hashed = hash_password(password);
    std::lock_guard lock(mutex_);
    Database db(database_, true);
    execute(db, "BEGIN IMMEDIATE");
    try {
      auto existing = users();
      bool bootstrap = existing.empty();
      if (!bootstrap &&
          (caller.is_null() || caller.value("role", Json{}) != "admin"))
        throw ApiError(403, "Admin privileges required");
      auto role = bootstrap ? Json("admin") : input.value("role", Json("user"));
      check(role == "admin" || role == "user", "Invalid role");
      execute(db,
              "INSERT INTO users(username,password_hash,role) VALUES(?,?,?)",
              {name, hashed, role});
      auto id = sqlite3_last_insert_rowid(db.value);
      execute(db, "COMMIT");
      return {{"msg", "User created"}, {"id", id}, {"role", role}};
    } catch (...) {
      execute(db, "ROLLBACK");
      throw;
    }
  }
  Json update_user(int64_t id, const Json &input, const Json &caller) {
    check(input.is_object(), "Invalid account fields");
    std::lock_guard lock(mutex_);
    Database db(database_, true);
    execute(db, "BEGIN IMMEDIATE");
    try {
      auto target =
          user("SELECT id,username,role,password_hash FROM users WHERE id=?",
               std::to_string(id), true);
      if (target.is_null())
        throw ApiError(404, "User not found");
      bool self = caller.value("id", Json{}) == id,
           admin = caller.value("role", Json{}) == "admin";
      if (!self && !admin)
        throw ApiError(403, "You can only edit your own account");
      check(input.contains("username") || input.contains("password") ||
                input.contains("role"),
            "No fields to update");
      if (input.contains("role")) {
        if (!admin)
          throw ApiError(403, "Only an admin can change roles");
        auto role = input["role"];
        check(role == "admin" || role == "user", "Invalid role");
        if (target["role"] == "admin" && role != "admin") {
          size_t admins = 0;
          for (auto &u : users())
            if (u["role"] == "admin")
              admins++;
          if (admins <= 1)
            throw ApiError(409, "Cannot demote the last remaining admin");
        }
        execute(db, "UPDATE users SET role=? WHERE id=?", {role, id});
      }
      if (input.contains("username")) {
        check(input["username"].is_string(), "Invalid username");
        auto name = input["username"].get<std::string>();
        check(!name.empty() && name.size() <= 4096, "Invalid username");
        execute(db, "UPDATE users SET username=? WHERE id=?", {name, id});
      }
      if (input.contains("password")) {
        check(input["password"].is_string(), "Invalid password");
        if (self) {
          check(input.value("current_password", Json{}).is_string(),
                "Current password required");
          if (!password_matches(target["password_hash"],
                                input["current_password"].get<std::string>() +
                                    pepper_))
            throw ApiError(403, "Current password is incorrect");
        }
        execute(db, "UPDATE users SET password_hash=? WHERE id=?",
                {hash_password(input["password"]), id});
      }
      execute(db, "COMMIT");
      return {{"msg", "User updated"}};
    } catch (...) {
      execute(db, "ROLLBACK");
      throw;
    }
  }
  Json delete_user(int64_t id, const Json &caller) {
    if (caller.value("role", Json{}) != "admin")
      throw ApiError(403, "Admin privileges required");
    if (caller.value("id", Json{}) == id)
      throw ApiError(403, "You cannot delete your own account");
    std::lock_guard lock(mutex_);
    Database db(database_, true);
    get_user(id);
    execute(db, "DELETE FROM users WHERE id=?", {id});
    return {{"msg", "User deleted successfully"}};
  }
  std::string login(const Json &input) {
    check(input.is_object() && input.value("username", Json{}).is_string() &&
              input.value("password", Json{}).is_string(),
          "Invalid credentials");
    auto username = input["username"].get<std::string>(),
         password = input["password"].get<std::string>();
    check(username.size() <= 4096 && password.size() <= 4096,
          "Invalid credentials");
    auto u = user(
        "SELECT id,username,role,password_hash FROM users WHERE username=?",
        username, true);
    check(!u.is_null() &&
              password_matches(u["password_hash"], password + pepper_),
          "Invalid credentials");
    auto now = std::time(nullptr);
    return sign_token({{"sub", std::to_string(u["id"].get<int64_t>())},
                       {"jti", random_id()},
                       {"type", "access"},
                       {"fresh", false},
                       {"iat", now},
                       {"nbf", now},
                       {"exp", now + 900}},
                      secret_);
  }
  Json authenticate(const std::string &token, bool ignore_expiry = false) {
    auto claim = verify_token(token, secret_, ignore_expiry);
    check(claim.value("sub", Json{}).is_string() &&
              claim.value("jti", Json{}).is_string(),
          "Unauthorized");
    {
      std::lock_guard lock(mutex_);
      check(!revoked_.contains(claim["jti"].get<std::string>()),
            "Unauthorized");
    }
    auto u = user("SELECT id,username,role FROM users WHERE id=?",
                  claim["sub"].get<std::string>());
    check(!u.is_null(), "Unauthorized");
    return u;
  }
  void logout(const std::string &token) {
    authenticate(token);
    auto claim = verify_token(token, secret_);
    std::lock_guard lock(mutex_);
    check(revoked_.size() < 100000, "Session revocation capacity reached");
    revoked_.insert(claim["jti"].get<std::string>());
  }
};
} // namespace stworkbench::runtime

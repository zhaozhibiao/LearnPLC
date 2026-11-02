// SPDX-License-Identifier: MIT
#include "../src/runtime_auth.hpp"
#include <cassert>
int main() {
  using namespace stworkbench::runtime;
  assert(b64url("hello") == "aGVsbG8");
  assert(unbase64("aGVsbG8") == "hello");
  assert(password_matches(
      "pbkdf2:sha256:1$salt$"
      "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b",
      "password"));
  assert(!password_matches(
      "pbkdf2:sha256:1$salt$"
      "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b",
      "wrong"));
  const auto jwt = sign_token({{"sub", "1"},
                               {"type", "access"},
                               {"exp", 9999999999LL},
                               {"jti", "test"}},
                              std::string(64, 'a'));
  assert(verify_token(jwt, std::string(64, 'a'))["sub"] == "1");
  bool bad = false;
  try {
    verify_token(jwt, std::string(64, 'b'));
  } catch (...) {
    bad = true;
  }
  assert(bad);
  bad = false;
  try {
    verify_token(
        sign_token({{"exp", 1}, {"type", "access"}}, std::string(64, 'a')),
        std::string(64, 'a'));
  } catch (...) {
    bad = true;
  }
  assert(bad);
  const auto directory = std::filesystem::temp_directory_path() /
                         ("st-auth-test-" + std::to_string(getpid()));
  std::filesystem::create_directory(directory);
  struct Cleanup {
    std::filesystem::path path;
    ~Cleanup() { std::filesystem::remove_all(path); }
  } cleanup{directory};
  stworkbench::atomic_write(directory / ".env",
                            "JWT_SECRET_KEY=" + std::string(64, 'a') +
                                "\nPEPPER=" + std::string(64, 'b') + "\n");
  sqlite3 *database = nullptr;
  assert(sqlite3_open((directory / "restapi.db").c_str(), &database) ==
         SQLITE_OK);
  assert(sqlite3_exec(
             database,
             "CREATE TABLE users(id INTEGER PRIMARY KEY,username TEXT NOT NULL "
             "UNIQUE,password_hash TEXT NOT NULL,role TEXT NOT NULL)",
             nullptr, nullptr, nullptr) == SQLITE_OK);
  sqlite3_close(database);
  Auth auth(directory);
  auto created = auth.create_user(
      {{"username", "operator"}, {"password", "test-password"}}, nullptr);
  auto admin_token =
      auth.login({{"username", "operator"}, {"password", "test-password"}});
  auto admin = auth.authenticate(admin_token);
  assert(admin["role"] == "admin");
  auto operator_record = auth.create_user({{"username", "user"},
                                           {"password", "another-password"},
                                           {"role", "user"}},
                                          admin);
  auto user_token =
      auth.login({{"username", "user"}, {"password", "another-password"}});
  auto user = auth.authenticate(user_token);
  bad = false;
  try {
    auth.delete_user(created["id"].get<int64_t>(), admin);
  } catch (...) {
    bad = true;
  }
  assert(bad);
  bad = false;
  try {
    auth.update_user(user["id"].get<int64_t>(), {{"role", "admin"}}, user);
  } catch (...) {
    bad = true;
  }
  assert(bad);
  auth.update_user(
      user["id"].get<int64_t>(),
      {{"password", "new-password"}, {"current_password", "another-password"}},
      user);
  auth.delete_user(user["id"].get<int64_t>(), admin);
  bad = false;
  try {
    auth.authenticate(user_token);
  } catch (...) {
    bad = true;
  }
  assert(bad);
}

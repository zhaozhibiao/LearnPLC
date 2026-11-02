// SPDX-License-Identifier: MIT
#define ST_SESSION_HELPER_NO_MAIN
#include "../src/session_helper.cpp"
#include <cassert>
int main() {
  using namespace stworkbench;
  assert(session::endpoint("192.168.1.31") == "https://192.168.1.31:18445");
  assert(session::endpoint("::1") == "https://[::1]:18445");
  auto reject = [](auto f) {
    bool bad = false;
    try {
      f();
    } catch (...) {
      bad = true;
    }
    assert(bad);
  };
  reject([] { session::endpoint("localhost/evil"); });
  reject([] { session::endpoint("127.0.0.1;id"); });
  session::validate_credentials(
      Json{{"username", "a"}, {"password", "private"}});
  reject([] {
    session::validate_credentials(
        Json{{"username", "a"}, {"password", "p"}, {"other", 1}});
  });
  reject([] {
    session::validate_credentials(
        Json{{"username", "a"}, {"password", nullptr}});
  });
  reject([] { session::validate_token("valid\nInjected"); });
  session::validate_token("a.b-c_d~+/=");
}

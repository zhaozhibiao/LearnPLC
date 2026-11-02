#include "stworkbench/common.hpp"
#include <cassert>
#include <iostream>
int main() {
  using namespace stworkbench;
  assert(sha256("abc") ==
         "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  assert(parse_json("{\"a\":1}")["a"] == 1);
  for (auto text : {"{\"a\":1,\"a\":2}", "{\"a\":{\"b\":1,\"b\":2}}", "[NaN]",
                    "[1e9999]"}) {
    bool failed = false;
    try {
      parse_json(text);
    } catch (const std::exception &) {
      failed = true;
    }
    assert(failed);
  }
  bool deep = false;
  try {
    parse_json("[[[[[]]]]]", 3);
  } catch (...) {
    deep = true;
  }
  assert(deep);
  auto ok = run_process({"/bin/echo", "native-no-shell"});
  assert(ok.exit_code == 0 && ok.output == "native-no-shell\n");
  auto limited =
      run_process({"/bin/echo", "123456789"}, {}, std::chrono::seconds(2), 4);
  assert(limited.truncated && limited.output.size() == 4);
  auto timeout =
      run_process({"/bin/sleep", "2"}, {}, std::chrono::milliseconds(20));
  assert(timeout.timed_out);
  auto input = run_process({"/bin/cat"}, {}, std::chrono::seconds(2), 100, true,
                           "stdin-json\n");
  assert(input.exit_code == 0 && input.output == "stdin-json\n");
  std::string large(256 * 1024, 'x');
  auto duplex = run_process({"/bin/cat"}, {}, std::chrono::seconds(2),
                            large.size(), true, large);
  assert(duplex.exit_code == 0 && duplex.output == large);
  auto infinite =
      run_process({"/usr/bin/yes"}, {}, std::chrono::milliseconds(30), 64);
  assert(infinite.timed_out && infinite.truncated &&
         infinite.output.size() == 64);
  auto clean =
      run_process({"/usr/bin/env"}, {}, std::chrono::seconds(2), 4096, true);
  assert(clean.exit_code == 0 &&
         clean.output == "PATH=/usr/sbin:/usr/bin:/sbin:/bin\nLANG=C.UTF-8\n");
  auto old_path = std::string(::getenv("PATH") ? ::getenv("PATH") : "");
  ::setenv("PATH", "/nonexistent", 1);
  auto fixed_path =
      run_process({"true"}, {}, std::chrono::seconds(2), 100, true);
  ::setenv("PATH", old_path.c_str(), 1);
  assert(fixed_path.exit_code == 0);
  std::cout
      << "strict JSON, SHA256, bounded process output and timeout passed\n";
}

// SPDX-License-Identifier: MIT
#include "hardware_private.hpp"
namespace stworkbench::hardware {
inline std::string trim(std::string s) {
  auto first = s.find_first_not_of(" \t\r\n");
  if (first == s.npos)
    return {};
  return s.substr(first, s.find_last_not_of(" \t\r\n") - first + 1);
}
inline void kernel_write(const fs::path &path, std::string_view value) {
  root_owned(path);
  Fd fd(::open(path.c_str(), O_WRONLY | O_NOFOLLOW | O_CLOEXEC));
  struct stat info{};
  require(fd && ::fstat(fd.get(), &info) == 0 && info.st_uid == 0 &&
              S_ISREG(info.st_mode),
          "Unsafe kernel control file");
  auto n = ::write(fd.get(), value.data(), value.size());
  require(n >= 0 && static_cast<std::size_t>(n) == value.size(),
          "Kernel control write failed");
}
inline Json isolate_cpu() {
  require(::geteuid() == 0, "CPU isolation requires root");
  constexpr auto housekeeping = "0-1,3-5,7";
  fs::path cpu = "/sys/devices/system/cpu";
  require(trim(capture(cpu / "cpu2/topology/thread_siblings_list", 4096)) ==
              "2,6",
          "CPU topology changed; refuse isolation");
  std::istringstream input(capture("/proc/cmdline", 65536));
  std::set<std::string> options;
  std::string option;
  while (input >> option)
    options.insert(option);
  for (auto required : {"isolcpus=domain,managed_irq,2,6", "nohz_full=2,6",
                        "rcu_nocbs=2,6", "irqaffinity=0-1,3-5,7"}) {
    require(options.contains(required),
            "Required isolation boot option missing");
  }
  kernel_write(cpu / "cpu6/online", "0");
  kernel_write("/sys/devices/virtual/workqueue/cpumask", "bb");
  Json failures = Json::array();
  root_owned("/proc/irq");
  for (const auto &entry : fs::directory_iterator("/proc/irq")) {
    auto name = entry.path().filename().string();
    if (name.empty() || name.find_first_not_of("0123456789") != name.npos)
      continue;
    try {
      kernel_write(entry.path() / "smp_affinity_list", housekeeping);
    } catch (const std::exception &) {
      failures.push_back({{"irq", name}, {"errno", errno}});
    }
  }
  Json report = {
      {"online", trim(capture(cpu / "online", 4096))},
      {"isolated", trim(capture(cpu / "isolated", 4096))},
      {"housekeeping", housekeeping},
      {"workqueueMask",
       trim(capture("/sys/devices/virtual/workqueue/cpumask", 4096))},
      {"irqWriteExceptions", failures}};
  root_owned("/run");
  atomic_write("/run/st-workbench-cpu-isolation.json", report.dump(2), 0644);
  return report;
}
} // namespace stworkbench::hardware
#include "cpu_management.hpp"
#ifndef ST_CPU_ISOLATION_NO_MAIN
int main(int argc, char **argv) {
  try {
    using namespace stworkbench::hardware;
    require(argc == 1 || argc == 2, "CPU isolation accepts one fixed action");
    std::string action = argc == 1 ? "--boot" : argv[1];
    stworkbench::Json result;
    if (action == "--boot")
      result = isolate_cpu();
    else if (action == "--install")
      result = install_cpu();
    else if (action == "--restrict-user-slice")
      result = restrict_user_slice();
    else if (action == "--verify")
      result = verify_cpu();
    else if (action == "--rollback")
      result = rollback_cpu();
    else
      throw std::runtime_error("Unknown CPU isolation action");
    std::cout << result.dump() << '\n';
    return 0;
  } catch (const std::exception &error) {
    std::cerr << error.what() << '\n';
    return 1;
  }
}

#endif

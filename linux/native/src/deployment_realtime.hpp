// SPDX-License-Identifier: MIT
#pragma once
#include "deployment_transport.hpp"
#include <sched.h>
#include <sys/utsname.h>
namespace stworkbench::deployment {
inline Json observed_service(const std::string &unit, bool user) {
  Json result = {
      {"Name", unit}, {"State", "unknown"}, {"Threads", Json::array()}};
  try {
    std::vector<std::string> args = {"/usr/bin/systemctl"};
    if (user)
      args.push_back("--user");
    args.insert(args.end(), {"show", unit, "--property=ActiveState",
                             "--property=ControlGroup"});
    auto output = process(args, {}, 4, 8192).output;
    std::map<std::string, std::string> fields;
    std::istringstream in(output);
    std::string line;
    while (std::getline(in, line)) {
      auto p = line.find('=');
      if (p != line.npos)
        fields[line.substr(0, p)] = line.substr(p + 1);
    }
    result["State"] = fields["ActiveState"];
    auto group = fields["ControlGroup"];
    if (group.empty())
      return result;
    require(group.front() == '/', "Invalid service cgroup");
    auto relative = fs::path(group).relative_path();
    safe_member(relative.generic_string());
    auto root = fs::path("/sys/fs/cgroup");
    auto directory = root / relative;
    if (!fs::is_directory(directory))
      directory = root / "systemd" / relative;
    std::vector<fs::path> queue = {directory};
    std::set<int> tids;
    std::size_t cursor = 0;
    while (cursor < queue.size() && cursor < 128 && tids.size() < 256) {
      auto current = queue[cursor++];
      auto source = current / "cgroup.threads";
      if (!fs::exists(source))
        source = current / "tasks";
      std::istringstream rows(read_file(source, 65536));
      std::string id;
      while (rows >> id && tids.size() < 256) {
        if (id.empty() || id.find_first_not_of("0123456789") != id.npos)
          continue;
        auto tid = std::stoll(id);
        if (tid > 0 && tid <= INT32_MAX)
          tids.insert(tid);
      }
      for (auto &e : fs::directory_iterator(current)) {
        if (queue.size() >= 128)
          break;
        if (e.is_directory() && !e.is_symlink())
          queue.push_back(e.path());
      }
    }
    for (auto tid : tids) {
      try {
        auto proc = fs::path("/proc") / std::to_string(tid);
        auto name = read_file(proc / "comm", 256);
        while (!name.empty() && (name.back() == '\n' || name.back() == '\r'))
          name.pop_back();
        auto policy = ::sched_getscheduler(tid);
        sched_param parameters{};
        require(policy >= 0 && ::sched_getparam(tid, &parameters) == 0,
                "Thread scheduling state unavailable");
        cpu_set_t cpus;
        CPU_ZERO(&cpus);
        require(::sched_getaffinity(tid, sizeof cpus, &cpus) == 0,
                "Thread affinity unavailable");
        Json list = Json::array();
        for (int cpu = 0; cpu < CPU_SETSIZE; ++cpu)
          if (CPU_ISSET(cpu, &cpus))
            list.push_back(cpu);
        std::map<int, std::string> names = {
            {0, "SCHED_OTHER"}, {1, "SCHED_FIFO"}, {2, "SCHED_RR"},
            {3, "SCHED_BATCH"}, {5, "SCHED_IDLE"}, {6, "SCHED_DEADLINE"}};
        result["Threads"].push_back(
            {{"Name", name},
             {"Tid", tid},
             {"Policy", names.contains(policy) ? names[policy] : "UNKNOWN"},
             {"Priority", parameters.sched_priority},
             {"Cpus", list}});
      } catch (...) {
      }
    }
  } catch (...) {
  }
  return result;
}
inline Json realtime_status() {
  utsname system{};
  auto release =
      ::uname(&system) == 0 ? std::string(system.release) : "unknown";
  Json realtime;
  try {
    auto value = read_file("/sys/kernel/realtime", 32);
    if (value == "1\n" || value == "1")
      realtime = true;
    else if (value == "0\n" || value == "0")
      realtime = false;
  } catch (...) {
  }
  auto plc = observed_service("stworkbench-openplc.service", true);
  auto motion = observed_service("st-workbench-is620n.service", false);
  return {{"KernelRelease", release},
          {"RealtimeKernel", realtime},
          {"CpuCount", sysconf(_SC_NPROCESSORS_ONLN)},
          {"PlcThreads", plc["Threads"]},
          {"MotionService", motion},
          {"MetricsAvailable", false},
          {"Message",
           "Thread observations are bounded and may be incomplete; "
           "cycle/jitter metrics are unavailable through this endpoint."}};
}
} // namespace stworkbench::deployment

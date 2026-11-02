// SPDX-License-Identifier: MIT
#pragma once
#include "hardware_operations.hpp"
namespace stworkbench::hardware {
inline constexpr const char *cpu_binary =
    "/usr/local/libexec/st-workbench-cpu-isolation";
inline constexpr const char *cpu_backups =
    "/var/backups/st-workbench-cpu-isolation";
inline constexpr const char *cpu_installation =
    "/var/backups/st-workbench-cpu-isolation/installation.json";
inline constexpr const char *cpu_params =
    "isolcpus=domain,managed_irq,2,6 nohz_full=2,6 rcu_nocbs=2,6 "
    "irqaffinity=0-1,3-5,7";
inline std::map<std::string, std::string> cpu_targets() {
  return {
      {"/etc/default/grub.d/90-st-workbench-isolation.cfg",
       std::string("# ST Workbench reserved physical "
                   "core\nGRUB_CMDLINE_LINUX_DEFAULT=\"${GRUB_CMDLINE_LINUX_"
                   "DEFAULT} ") +
           cpu_params + "\"\n"},
      {"/etc/systemd/system.conf.d/90-st-workbench-isolation.conf",
       "[Manager]\nCPUAffinity=\nCPUAffinity=0-1 3-5 7\n"},
      {"/etc/systemd/user.conf.d/90-st-workbench-isolation.conf",
       "[Manager]\nCPUAffinity=\nCPUAffinity=0-1 3-5 7\n"},
      {"/etc/systemd/system/user.slice.d/90-st-workbench-isolation.conf",
       "[Slice]\nAllowedCPUs=0-1 3-5 7\n"},
      {"/etc/systemd/system/st-workbench-cpu-isolation.service",
       "[Unit]\nDescription=Reserve physical core for ST Workbench "
       "motion\nAfter=local-fs.target\nBefore=st-workbench-is620n.service\n["
       "Service]\nType=oneshot\nExecStart=/usr/local/libexec/"
       "st-workbench-cpu-isolation "
       "--boot\nRemainAfterExit=yes\n[Install]\nWantedBy=multi-user.target\n"}};
}
inline void root_parents(const fs::path &p) {
  if (fs::exists(p)) {
    root_owned(p);
    return;
  }
  root_parents(p.parent_path());
  mkdir_root(p);
}
inline Json cpu_preflight() {
  require(::geteuid() == 0, "CPU installer requires root");
  require(
      trim(capture("/sys/devices/system/cpu/cpu2/topology/thread_siblings_list",
                   4096)) == "2,6",
      "CPU topology changed");
  auto running = command({"/usr/bin/pgrep", "-x", "ecmotion-rt"}, false);
  require(running.exit_code == 1, "Motion core must be absent");
  auto state = plc_status();
  require(state == "STOPPED" || state == "EMPTY",
          "PLC must be stopped or empty");
  auto drive = command({"/opt/etherlab/bin/ethercat", "upload", "-p", "0", "-t",
                        "uint16", "0x6041", "0"})
                   .output;
  std::istringstream in(drive);
  std::string part, last;
  while (in >> part)
    last = part;
  std::size_t used = 0;
  auto status = std::stoul(last, &used, 10);
  require(used == last.size() && status <= 65535 && !(status & 4),
          "Drive operation enabled or unconfirmed");
  return {{"plc", "STATUS:" + state},
          {"driveStatus", trim(drive)},
          {"bootId", trim(capture("/proc/sys/kernel/random/boot_id", 4096))}};
}
inline void cpu_reload(bool update_grub) {
  if (update_grub) {
    command({"/usr/sbin/update-grub"});
    root_owned("/boot/grub/grub.cfg");
    auto generated = capture("/boot/grub/grub.cfg");
    require(generated.find(cpu_params) != generated.npos,
            "Generated GRUB lacks CPU options");
    command({"/usr/bin/grub-script-check", "/boot/grub/grub.cfg"});
  }
  command({"/usr/bin/systemctl", "daemon-reload"});
}
inline Json install_cpu() {
  ApplyLock lock;
  auto report = cpu_preflight();
  root_owned(cpu_binary);
  auto binary = capture(cpu_binary, 128 * 1024 * 1024);
  require(binary.starts_with(std::string("\177ELF", 4)),
          "Installed CPU helper must be native ELF");
  char self[4096];
  auto n = ::readlink("/proc/self/exe", self, sizeof self - 1);
  require(n > 0 && std::size_t(n) < sizeof self - 1,
          "Cannot identify CPU installer");
  self[n] = 0;
  root_owned(self);
  require(sha256(capture(self, 128 * 1024 * 1024)) == sha256(binary),
          "Install must execute approved CPU binary");
  root_parents(cpu_backups);
  TempDir transaction(cpu_backups, "installation-");
  auto targets = cpu_targets();
  Json originals = Json::object();
  for (auto &[name, text] : targets) {
    fs::path p = name;
    root_parents(p.parent_path());
    struct stat s{};
    if (::lstat(p.c_str(), &s) == 0) {
      root_owned(p);
      originals[name] = capture(p);
    } else {
      require(errno == ENOENT, "Cannot inspect CPU target");
      originals[name] = nullptr;
    }
  }
  root_owned("/boot/grub/grub.cfg");
  atomic_write(transaction.path / "grub.cfg", capture("/boot/grub/grub.cfg"));
  atomic_write(transaction.path / "originals.json", originals.dump());
  // The backup is retained before the first persistent configuration change.
  auto retained =
      fs::path(cpu_backups) / ("backup-" + std::to_string(monotonic_ns()));
  require(::rename(transaction.path.c_str(), retained.c_str()) == 0,
          "CPU backup publish failed");
  Fd backup_directory(::open(cpu_backups, O_RDONLY | O_DIRECTORY | O_CLOEXEC));
  require(backup_directory && ::fsync(backup_directory.get()) == 0,
          "CPU backup directory fsync failed");
  report.update({{"backup", retained.string()},
                 {"parameters", cpu_params},
                 {"installed", false},
                 {"rebootRequired", true},
                 {"binarySha256", sha256(binary)}});
  atomic_write(cpu_installation, report.dump());
  for (auto &[name, text] : targets)
    atomic_write(name, text, 0644);
  command({"/usr/bin/systemd-analyze", "verify",
           "/etc/systemd/system/st-workbench-cpu-isolation.service"});
  cpu_reload(true);
  command(
      {"/usr/bin/systemctl", "enable", "st-workbench-cpu-isolation.service"});
  report["installed"] = true;
  atomic_write(cpu_installation, report.dump());
  atomic_write(retained / "installation.json", report.dump());
  return report;
}
inline Json restrict_user_slice() {
  require(::geteuid() == 0, "Root required");
  ApplyLock lock;
  root_owned(cpu_installation, 0600);
  auto record = object(capture(cpu_installation));
  auto backup = record.at("backup").get<std::string>();
  auto prefix = std::string(cpu_backups) + "/backup-";
  require(backup.starts_with(prefix) && backup.size() > prefix.size() &&
              backup.substr(prefix.size()).find_first_not_of("0123456789") ==
                  std::string::npos,
          "Invalid root backup identity");
  root_owned(fs::path(backup) / "originals.json", 0600);
  auto originals = object(capture(fs::path(backup) / "originals.json"));
  const std::string target =
      "/etc/systemd/system/user.slice.d/90-st-workbench-isolation.conf";
  require(originals.contains(target), "CPU installation lacks slice backup");
  root_owned(fs::path(target).parent_path());
  if (fs::exists(target))
    root_owned(target);
  atomic_write(target, cpu_targets().at(target), 0644);
  cpu_reload(false);
  command({"/usr/bin/systemctl", "set-property", "--runtime", "user.slice",
           "AllowedCPUs=0-1,3-5,7"});
  return {
      {"effectiveCpus",
       trim(capture("/sys/fs/cgroup/user.slice/cpuset.cpus.effective", 4096))}};
}
inline Json rollback_cpu() {
  require(::geteuid() == 0, "Root required");
  ApplyLock lock;
  cpu_preflight();
  root_owned(cpu_installation, 0600);
  auto record = object(capture(cpu_installation));
  auto backup = record.at("backup").get<std::string>();
  auto prefix = std::string(cpu_backups) + "/backup-";
  require(backup.starts_with(prefix) && backup.size() > prefix.size() &&
              backup.substr(prefix.size()).find_first_not_of("0123456789") ==
                  std::string::npos,
          "Invalid root backup identity");
  auto saved = fs::path(backup) / "originals.json";
  root_owned(saved, 0600);
  auto originals = object(capture(saved));
  auto targets = cpu_targets();
  require(originals.size() == targets.size(),
          "Incomplete CPU rollback inventory");
  for (auto &[name, text] : targets) {
    require(originals.contains(name) &&
                (originals[name].is_string() || originals[name].is_null()),
            "Invalid CPU rollback inventory");
    root_owned(fs::path(name).parent_path());
    struct stat info{};
    if (::lstat(name.c_str(), &info) == 0)
      root_owned(name);
    else
      require(errno == ENOENT, "Cannot inspect CPU rollback target");
  }
  command(
      {"/usr/bin/systemctl", "disable", "st-workbench-cpu-isolation.service"},
      false);
  for (auto &[name, text] : targets) {
    if (originals[name].is_null()) {
      if (::unlink(name.c_str()) != 0)
        require(errno == ENOENT, "CPU rollback unlink failed");
      Fd dir(::open(fs::path(name).parent_path().c_str(),
                    O_RDONLY | O_DIRECTORY | O_CLOEXEC));
      require(dir && ::fsync(dir.get()) == 0,
              "CPU rollback directory fsync failed");
    } else
      atomic_write(name, originals[name].get<std::string>(), 0644);
  }
  kernel_write("/sys/devices/system/cpu/cpu6/online", "1");
  command({"/usr/sbin/update-grub"});
  command({"/usr/bin/grub-script-check", "/boot/grub/grub.cfg"});
  cpu_reload(false);
  record["installed"] = false;
  record["rollbackConfigured"] = true;
  record["rebootRequired"] = true;
  atomic_write(cpu_installation, record.dump());
  return record;
}
inline Json verify_cpu() {
  Json r = {
      {"bootId", trim(capture("/proc/sys/kernel/random/boot_id", 4096))},
      {"kernelCommandLine", trim(capture("/proc/cmdline", 65536))},
      {"online", trim(capture("/sys/devices/system/cpu/online", 4096))},
      {"isolated", trim(capture("/sys/devices/system/cpu/isolated", 4096))},
      {"cpu6Online",
       trim(capture("/sys/devices/system/cpu/cpu6/online", 4096))},
      {"workqueueMask",
       trim(capture("/sys/devices/virtual/workqueue/cpumask", 4096))},
      {"defaultIrqMask", trim(capture("/proc/irq/default_smp_affinity", 4096))},
      {"plcState", plc_status()},
      {"normalProcessAffinityExceptions", Json::array()}};
  for (auto &p : fs::directory_iterator("/proc")) {
    auto pid = p.path().filename().string();
    if (pid.empty() || pid.find_first_not_of("0123456789") != pid.npos)
      continue;
    try {
      if (capture(p.path() / "cmdline", 1024 * 1024).empty())
        continue;
      for (auto &t : fs::directory_iterator(p.path() / "task")) {
        auto text = capture(t.path() / "status", 65536);
        std::istringstream in(text);
        std::string line, cpus;
        while (std::getline(in, line))
          if (line.starts_with("Cpus_allowed_list:")) {
            cpus = trim(line.substr(18));
            break;
          }
        if (!cpus.empty() && cpus != "0-1,3-5,7")
          r["normalProcessAffinityExceptions"].push_back(
              {{"pid", pid},
               {"tid", t.path().filename().string()},
               {"cpus", cpus}});
      }
    } catch (const std::exception &) {
    } // Processes may exit during this read-only walk.
  }
  require(r["cpu6Online"] == "0" &&
              (r["plcState"] == "STOPPED" || r["plcState"] == "EMPTY") &&
              r["normalProcessAffinityExceptions"].empty(),
          "CPU postboot verification failed");
  r["verified"] = true;
  root_owned("/run");
  atomic_write("/run/st-workbench-cpu-verification.json", r.dump(2), 0644);
  return r;
}
} // namespace stworkbench::hardware

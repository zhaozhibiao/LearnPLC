// SPDX-License-Identifier: MIT
#include "stworkbench/common.hpp"
#include <cerrno>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <fcntl.h>
#include <poll.h>
#include <set>
#include <signal.h>
#include <spawn.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <thread>
extern char **environ;
namespace stworkbench {
namespace {
[[noreturn]] void fail(const char *what) {
  throw std::runtime_error(std::string(what) + ": " + std::strerror(errno));
}
void no_links(const std::filesystem::path &p) {
  auto path = std::filesystem::absolute(p).lexically_normal();
  for (auto current = path; !current.empty(); current = current.parent_path()) {
    struct stat st{};
    if (::lstat(current.c_str(), &st) == 0 && S_ISLNK(st.st_mode))
      throw std::runtime_error("Symlink in protected file path");
    if (current == current.root_path())
      break;
  }
}
void write_all(int fd, std::string_view bytes) {
  while (!bytes.empty()) {
    auto n = ::write(fd, bytes.data(), bytes.size());
    if (n < 0 && errno == EINTR)
      continue;
    if (n <= 0)
      fail("write");
    bytes.remove_prefix(static_cast<std::size_t>(n));
  }
}
} // namespace
Json parse_json(std::string_view text, std::size_t maximum_depth) {
  struct Object {
    bool object;
    std::set<std::string> keys;
  };
  std::vector<Object> stack;
  auto callback = [&](int, Json::parse_event_t event, Json &value) {
    if (event == Json::parse_event_t::object_start ||
        event == Json::parse_event_t::array_start) {
      if (stack.size() >= maximum_depth)
        throw std::runtime_error("JSON depth exceeds limit");
      stack.push_back({event == Json::parse_event_t::object_start, {}});
    } else if (event == Json::parse_event_t::key) {
      if (stack.empty() || !stack.back().object ||
          !stack.back().keys.insert(value.get<std::string>()).second)
        throw std::runtime_error("Duplicate JSON field");
    } else if (event == Json::parse_event_t::object_end ||
               event == Json::parse_event_t::array_end) {
      if (stack.empty())
        throw std::runtime_error("Invalid JSON nesting");
      stack.pop_back();
    } else if (event == Json::parse_event_t::value && value.is_number_float() &&
               !std::isfinite(value.get<double>()))
      throw std::runtime_error("Nonfinite JSON value");
    return true;
  };
  try {
    return Json::parse(text.begin(), text.end(), callback, true, false);
  } catch (const Json::exception &) {
    throw std::runtime_error("Invalid bounded JSON document");
  }
}
std::string read_file(const std::filesystem::path &path, std::size_t maximum,
                      bool private_file) {
  no_links(path);
  Fd fd(::open(path.c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK));
  if (!fd)
    fail("open regular file");
  struct stat st{};
  if (::fstat(fd.get(), &st) != 0)
    fail("fstat");
  if (!S_ISREG(st.st_mode) || st.st_size < 0 ||
      static_cast<std::uintmax_t>(st.st_size) > maximum)
    throw std::runtime_error("File type or capacity invalid");
  if (private_file && (st.st_uid != ::geteuid() || (st.st_mode & 0777) != 0600))
    throw std::runtime_error("File must be owned and mode0600");
  std::string result;
  char buffer[65536];
  for (;;) {
    auto n = ::read(fd.get(), buffer, sizeof buffer);
    if (n < 0 && errno == EINTR)
      continue;
    if (n < 0)
      fail("read file");
    if (!n)
      break;
    if (result.size() + static_cast<std::size_t>(n) > maximum)
      throw std::runtime_error("File exceeds capacity");
    result.append(buffer, static_cast<std::size_t>(n));
  }
  return result;
}
void atomic_write(const std::filesystem::path &target, std::string_view bytes,
                  unsigned mode) {
  no_links(target);
  auto parent = target.parent_path();
  if (parent.empty())
    parent = ".";
  std::string pattern =
      (parent / ("." + target.filename().string() + "-XXXXXX")).string();
  std::vector<char> name(pattern.begin(), pattern.end());
  name.push_back(0);
  Fd fd(::mkstemp(name.data()));
  if (!fd)
    fail("create atomic temporary");
  try {
    if (::fchmod(fd.get(), static_cast<mode_t>(mode)) != 0)
      fail("chmod temporary");
    write_all(fd.get(), bytes);
    if (::fsync(fd.get()) != 0)
      fail("sync temporary");
    no_links(target);
    if (::rename(name.data(), target.c_str()) != 0)
      fail("publish file");
    Fd directory(::open(parent.c_str(),
                        O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC));
    if (!directory || ::fsync(directory.get()) != 0)
      fail("sync directory");
  } catch (...) {
    ::unlink(name.data());
    throw;
  }
}
std::int64_t monotonic_ns() {
  return std::chrono::duration_cast<std::chrono::nanoseconds>(
             std::chrono::steady_clock::now().time_since_epoch())
      .count();
}
ProcessResult run_process(const std::vector<std::string> &args,
                          const std::filesystem::path &cwd,
                          std::chrono::milliseconds timeout,
                          std::size_t maximum, bool clean_env,
                          std::string_view input) {
  if (args.empty() || args.front().empty() || timeout.count() <= 0)
    throw std::invalid_argument("Invalid bounded process request");
  for (auto &argument : args)
    if (argument.find('\0') != std::string::npos)
      throw std::invalid_argument("Embedded NUL in argv");
  int pipefd[2];
  if (::pipe2(pipefd, O_CLOEXEC) != 0)
    fail("pipe");
  Fd readfd(pipefd[0]), writefd(pipefd[1]);
  int inputfd[2];
  if (::socketpair(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0, inputfd) != 0)
    fail("process input socket");
  Fd child_input(inputfd[0]), parent_input(inputfd[1]);
  std::vector<char *> argv;
  for (auto &argument : args)
    argv.push_back(const_cast<char *>(argument.c_str()));
  argv.push_back(nullptr);
  auto executable = args.front();
  if (clean_env && executable.find('/') == std::string::npos) {
    bool found = false;
    for (const auto directory : {"/usr/sbin", "/usr/bin", "/sbin", "/bin"}) {
      auto candidate = std::string(directory) + "/" + executable;
      struct stat st{};
      if (::stat(candidate.c_str(), &st) == 0 && S_ISREG(st.st_mode) &&
          ::access(candidate.c_str(), X_OK) == 0) {
        executable = std::move(candidate);
        found = true;
        break;
      }
    }
    if (!found)
      throw std::runtime_error("Executable unavailable in fixed process PATH");
  }
  posix_spawn_file_actions_t actions;
  posix_spawnattr_t attributes;
  auto check = [](int result) {
    if (result)
      throw std::runtime_error(std::string("Cannot launch bounded process: ") +
                               std::strerror(result));
  };
  check(::posix_spawn_file_actions_init(&actions));
  const auto attr_result = ::posix_spawnattr_init(&attributes);
  if (attr_result) {
    ::posix_spawn_file_actions_destroy(&actions);
    check(attr_result);
  }
  pid_t pid = -1;
  try {
    check(::posix_spawn_file_actions_adddup2(&actions, child_input.get(),
                                             STDIN_FILENO));
    check(::posix_spawn_file_actions_adddup2(&actions, writefd.get(),
                                             STDOUT_FILENO));
    check(::posix_spawn_file_actions_adddup2(&actions, writefd.get(),
                                             STDERR_FILENO));
    if (!cwd.empty())
      check(::posix_spawn_file_actions_addchdir_np(&actions, cwd.c_str()));
    check(::posix_spawnattr_setflags(&attributes, POSIX_SPAWN_SETPGROUP));
    check(::posix_spawnattr_setpgroup(&attributes, 0));
    char path[] = "PATH=/usr/sbin:/usr/bin:/sbin:/bin";
    char language[] = "LANG=C.UTF-8";
    char *environment[] = {path, language, nullptr};
    if (clean_env)
      check(::posix_spawn(&pid, executable.c_str(), &actions, &attributes,
                          argv.data(), environment));
    else
      check(::posix_spawnp(&pid, argv.front(), &actions, &attributes,
                           argv.data(), environ));
  } catch (...) {
    ::posix_spawn_file_actions_destroy(&actions);
    ::posix_spawnattr_destroy(&attributes);
    throw;
  }
  ::posix_spawn_file_actions_destroy(&actions);
  ::posix_spawnattr_destroy(&attributes);
  child_input.reset();
  writefd.reset();
  const auto until = std::chrono::steady_clock::now() + timeout;
  ProcessResult result;
  bool exited = false, eof = false;
  int status = 0;
  std::size_t input_offset = 0;
  try {
    int flags = ::fcntl(readfd.get(), F_GETFL, 0);
    if (flags < 0 || ::fcntl(readfd.get(), F_SETFL, flags | O_NONBLOCK) < 0)
      fail("nonblocking process pipe");
    while (!exited || !eof) {
      if (std::chrono::steady_clock::now() >= until) {
        result.timed_out = true;
        ::kill(-pid, SIGKILL);
        if (!exited) {
          while (::waitpid(pid, &status, 0) < 0 && errno == EINTR) {
          }
          exited = true;
        }
        break;
      }
      char buffer[8192];
      // Bound each drain pass so an endless producer cannot starve the
      // deadline.
      for (unsigned reads = 0; reads < 8; ++reads) {
        auto n = ::read(readfd.get(), buffer, sizeof buffer);
        if (n > 0) {
          auto allowed = std::min(maximum - result.output.size(),
                                  static_cast<std::size_t>(n));
          result.output.append(buffer, allowed);
          result.truncated |= allowed < static_cast<std::size_t>(n);
          continue;
        }
        if (!n)
          eof = true;
        else if (errno != EAGAIN && errno != EINTR)
          fail("read process pipe");
        break;
      }
      if (parent_input) {
        if (input_offset == input.size()) {
          ::shutdown(parent_input.get(), SHUT_WR);
          parent_input.reset();
        } else {
          const auto n =
              ::send(parent_input.get(), input.data() + input_offset,
                     input.size() - input_offset, MSG_DONTWAIT | MSG_NOSIGNAL);
          if (n > 0)
            input_offset += static_cast<std::size_t>(n);
          else if (n < 0 && errno != EAGAIN && errno != EINTR)
            parent_input.reset();
        }
      }
      if (!exited) {
        auto observed = ::waitpid(pid, &status, WNOHANG);
        if (observed == pid)
          exited = true;
        else if (observed < 0 && errno != EINTR)
          fail("wait process");
      }
      if (!exited || !eof) {
        pollfd events[] = {{readfd.get(), POLLIN, 0},
                           {parent_input.get(), POLLOUT, 0}};
        ::poll(events, parent_input ? 2 : 1, 10);
      }
    }
    result.exit_code = WIFEXITED(status)     ? WEXITSTATUS(status)
                       : WIFSIGNALED(status) ? 128 + WTERMSIG(status)
                                             : -1;
    return result;
  } catch (...) {
    ::kill(-pid, SIGKILL);
    if (!exited)
      while (::waitpid(pid, &status, 0) < 0 && errno == EINTR) {
      }
    throw;
  }
}
} // namespace stworkbench

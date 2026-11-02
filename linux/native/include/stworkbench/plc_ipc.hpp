// SPDX-License-Identifier: MIT
#pragma once
#include "common.hpp"
#include <cerrno>
#include <cstring>
#include <mutex>
#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>

namespace stworkbench {
// One connection owns a full request/reply. A failed transaction is never
// replayed.
class PlcIpc {
  std::string path_;
  Fd socket_;
  std::timed_mutex mutex_;
  using Clock = std::chrono::steady_clock;
  static void wait(int fd, short events, Clock::time_point deadline) {
    for (;;) {
      const auto remaining = deadline - Clock::now();
      if (remaining <= Clock::duration::zero())
        throw std::runtime_error(
            "PLC IPC deadline exceeded; state may be unknown");
      pollfd p{fd, events, 0};
      auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(remaining)
                    .count();
      const auto result =
          ::poll(&p, 1, static_cast<int>(std::min<int64_t>(ms + 1, 60000)));
      if (result < 0 && errno == EINTR)
        continue;
      if (result < 0)
        throw std::runtime_error("PLC IPC poll failed");
      if (result && (p.revents & events))
        return;
      if (result && (p.revents & (POLLERR | POLLHUP | POLLNVAL)))
        throw std::runtime_error("PLC IPC disconnected; state may be unknown");
    }
  }
  void connect(Clock::time_point deadline) {
    if (socket_)
      return;
    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    std::copy(path_.begin(), path_.end(), addr.sun_path);
    socket_.reset(
        ::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0));
    if (!socket_)
      throw std::runtime_error("Cannot open PLC IPC socket");
    if (::connect(socket_.get(), reinterpret_cast<sockaddr *>(&addr),
                  sizeof addr) != 0) {
      if (errno != EINPROGRESS)
        throw std::runtime_error("PLC IPC is unavailable");
      wait(socket_.get(), POLLOUT, deadline);
      int error = 0;
      socklen_t size = sizeof error;
      if (::getsockopt(socket_.get(), SOL_SOCKET, SO_ERROR, &error, &size) !=
              0 ||
          error)
        throw std::runtime_error("PLC IPC connect failed");
    }
  }

public:
  explicit PlcIpc(std::string path) : path_(std::move(path)) {
    if (path_.empty() || path_.size() >= sizeof(sockaddr_un::sun_path) ||
        path_.find('\0') != std::string::npos)
      throw std::invalid_argument("Invalid PLC IPC path");
  }
  std::string
  request(std::string_view command,
          std::chrono::milliseconds timeout = std::chrono::milliseconds(500)) {
    if (command.empty() || command.size() > 65536 || timeout.count() <= 0 ||
        command.find_first_of("\r\n") != std::string_view::npos ||
        command.find('\0') != std::string_view::npos)
      throw std::invalid_argument("Invalid PLC IPC command");
    const auto deadline = Clock::now() + timeout;
    std::unique_lock<std::timed_mutex> guard(mutex_, std::defer_lock);
    if (!guard.try_lock_until(deadline))
      throw std::runtime_error(
          "PLC IPC queue deadline exceeded; command not sent");
    try {
      connect(deadline);
      std::string frame(command);
      frame += '\n';
      std::size_t sent = 0;
      while (sent < frame.size()) {
        wait(socket_.get(), POLLOUT, deadline);
        const auto n = ::send(socket_.get(), frame.data() + sent,
                              frame.size() - sent, MSG_NOSIGNAL);
        if (n < 0 && (errno == EINTR || errno == EAGAIN))
          continue;
        if (n <= 0)
          throw std::runtime_error("PLC IPC send failed; state may be unknown");
        sent += static_cast<std::size_t>(n);
      }
      std::string reply;
      for (;;) {
        wait(socket_.get(), POLLIN, deadline);
        char buffer[8192];
        const auto n = ::recv(socket_.get(), buffer, sizeof buffer, 0);
        if (n < 0 && (errno == EINTR || errno == EAGAIN))
          continue;
        if (n <= 0)
          throw std::runtime_error("PLC IPC reply lost; state may be unknown");
        reply.append(buffer, static_cast<std::size_t>(n));
        if (reply.size() > 65536 + 256)
          throw std::runtime_error("PLC IPC response exceeds limit");
        const auto newline = reply.find('\n');
        if (newline != std::string::npos) {
          if (newline != reply.size() - 1 ||
              reply.find('\0') != std::string::npos)
            throw std::runtime_error("Unexpected PLC IPC response framing");
          reply.resize(newline);
          if (!reply.empty() && reply.back() == '\r')
            reply.pop_back();
          return reply;
        }
      }
    } catch (...) {
      socket_.reset();
      throw;
    }
  }
};
} // namespace stworkbench

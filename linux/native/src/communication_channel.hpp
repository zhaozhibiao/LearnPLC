// SPDX-License-Identifier: MIT
#pragma once
#include "communication_protocol.hpp"
#include "stworkbench/common.hpp"
#include <algorithm>
#include <arpa/inet.h>
#include <atomic>
#include <fcntl.h>
#include <linux/serial.h>
#include <map>
#include <mutex>
#include <poll.h>
#include <sys/file.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <termios.h>
#include <thread>
namespace stcommunication {
using stworkbench::Json;
inline double clock_now() { return stworkbench::monotonic_ns() / 1e9; }
class Channel {
  Json config;
  stworkbench::Fd fd;
  Bytes buffer;
  unsigned transaction = 0;
  std::atomic<unsigned> generation{0};
  std::mutex lock;
  std::atomic<bool> poisoned{false};
  void fail(unsigned g) {
    std::lock_guard guard(lock);
    if (g == generation) {
      ++generation;
      fd.reset();
      poisoned = true;
    }
  }
  void check(unsigned g) {
    if (g != generation)
      throw Error(6004, "Cancelled");
  }
  int resource(unsigned g) {
    std::lock_guard guard(lock);
    check(g);
    return fd.get();
  }
  void wait(bool read, unsigned g, double deadline) {
    for (;;) {
      double left = deadline - clock_now();
      if (left <= 0)
        throw Error(6003, "Timeout");
      pollfd p{resource(g), short(read ? POLLIN : POLLOUT), 0};
      int n = poll(&p, 1, std::max(1, int(std::min(left, .02) * 1000)));
      check(g);
      if (n > 0)
        return;
      if (n < 0 && errno != EINTR)
        throw Error(6004, "Poll");
    }
  }
  void delay(double seconds, unsigned g, double deadline) {
    double end = clock_now() + seconds;
    while (clock_now() < end) {
      check(g);
      if (clock_now() >= deadline)
        throw Error(6003, "Timeout");
      std::this_thread::sleep_for(std::chrono::microseconds(500));
    }
  }
  Bytes read(size_t max, unsigned g, double deadline) {
    for (;;) {
      wait(true, g, deadline);
      Bytes b(max);
      ssize_t n;
      {
        std::lock_guard guard(lock);
        check(g);
        n = ::read(fd.get(), b.data(), max);
      }
      if (n < 0 && (errno == EAGAIN || errno == EINTR))
        continue;
      if (n <= 0)
        throw Error(6004, "Disconnected");
      b.resize(n);
      return b;
    }
  }
  Bytes exact(size_t count, unsigned g, double deadline) {
    while (buffer.size() < count) {
      auto b = read(count - buffer.size(), g, deadline);
      buffer.insert(buffer.end(), b.begin(), b.end());
    }
    Bytes b(buffer.begin(), buffer.begin() + count);
    buffer.erase(buffer.begin(), buffer.begin() + count);
    return b;
  }
  bool serial() const {
    return config["Protocol"] == "Serial" || config["Protocol"] == "ModbusRtu";
  }
  void send(const Bytes &b, unsigned g, double deadline, bool &sent) {
    bool automatic = serial() && config["Direction"] == "Automatic";
    if (automatic)
      delay(config["BeforeSendMicroseconds"].get<double>() / 1e6, g, deadline);
    size_t offset = 0;
    while (offset < b.size()) {
      wait(false, g, deadline);
      ssize_t n;
      {
        std::lock_guard guard(lock);
        check(g);
        n = ::write(fd.get(), b.data() + offset, b.size() - offset);
      }
      if (n < 0 && (errno == EAGAIN || errno == EINTR))
        continue;
      if (n <= 0)
        throw Error(6004, "Send failed");
      sent = true;
      offset += n;
    }
    if (serial()) {
      for (;;) {
        int pending = 0;
        if (ioctl(resource(g), TIOCOUTQ, &pending))
          throw Error(6004, "Serial drain");
        if (!pending)
          break;
        delay(.001, g, deadline);
      }
      if (automatic)
        delay(config["AfterSendMicroseconds"].get<double>() / 1e6, g, deadline);
    }
  }
  void open(unsigned g, double deadline) {
    std::unique_lock guard(lock);
    check(g);
    if (!serial()) {
      std::string host = config["Host"];
      int family = host.find(':') == std::string::npos ? AF_INET : AF_INET6;
      fd.reset(socket(family, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0));
      int result;
      if (family == AF_INET) {
        sockaddr_in a{};
        a.sin_family = family;
        a.sin_port = htons(config["Port"].get<int>());
        if (inet_pton(family, host.c_str(), &a.sin_addr) != 1)
          throw Error(6007, "Host");
        result = connect(fd.get(), reinterpret_cast<sockaddr *>(&a), sizeof(a));
      } else {
        sockaddr_in6 a{};
        a.sin6_family = family;
        a.sin6_port = htons(config["Port"].get<int>());
        if (inet_pton(family, host.c_str(), &a.sin6_addr) != 1)
          throw Error(6007, "Host");
        result = connect(fd.get(), reinterpret_cast<sockaddr *>(&a), sizeof(a));
      }
      if (result && errno != EINPROGRESS)
        throw Error(6004, "Connect");
      guard.unlock();
      wait(false, g, deadline);
      guard.lock();
      check(g);
      int error = 0;
      socklen_t size = sizeof(error);
      if (getsockopt(fd.get(), SOL_SOCKET, SO_ERROR, &error, &size) || error)
        throw Error(6004, "Connect");
      return;
    }
    fd.reset(::open(config["Device"].get<std::string>().c_str(),
                    O_RDWR | O_NOCTTY | O_NONBLOCK | O_CLOEXEC));
    struct stat info{};
    if (fd.get() < 0 || fstat(fd.get(), &info) || !S_ISCHR(info.st_mode) ||
        flock(fd.get(), LOCK_EX | LOCK_NB) || ioctl(fd.get(), TIOCEXCL))
      throw Error(6004, "Serial ownership");
    termios t{};
    if (tcgetattr(fd.get(), &t))
      throw Error(6004, "Serial format");
    cfmakeraw(&t);
    t.c_cflag = CLOCAL | CREAD | (config["DataBits"] == 7 ? CS7 : CS8);
    if (config["StopBits"] == 2)
      t.c_cflag |= CSTOPB;
    if (config["Parity"] != "None")
      t.c_cflag |= PARENB;
    if (config["Parity"] == "Odd")
      t.c_cflag |= PARODD;
    std::map<int, speed_t> speeds{
        {1200, B1200},   {2400, B2400},     {4800, B4800},
        {9600, B9600},   {19200, B19200},   {38400, B38400},
        {57600, B57600}, {115200, B115200}, {230400, B230400}};
    auto speed = speeds.at(config["BaudRate"].get<int>());
    cfsetispeed(&t, speed);
    cfsetospeed(&t, speed);
    t.c_cc[VMIN] = t.c_cc[VTIME] = 0;
    if (tcsetattr(fd.get(), TCSANOW, &t) || tcflush(fd.get(), TCIOFLUSH))
      throw Error(6004, "Serial configure");
    if (config["Physical"] == "RS485" && config["Direction"] == "KernelRts") {
      serial_rs485 rs{};
      rs.flags = SER_RS485_ENABLED | SER_RS485_RTS_ON_SEND;
      rs.delay_rts_before_send =
          (config["BeforeSendMicroseconds"].get<unsigned>() + 999) / 1000;
      rs.delay_rts_after_send =
          (config["AfterSendMicroseconds"].get<unsigned>() + 999) / 1000;
      if (ioctl(fd.get(), TIOCSRS485, &rs))
        throw Error(6004, "RS485 configure");
    }
  }
  double interval(bool gap) {
    int baud = config["BaudRate"];
    if (baud > 19200)
      return gap ? .00175 : .00075;
    return (gap ? 3.5 : 1.5) *
           (1 + config["DataBits"].get<int>() + (config["Parity"] != "None") +
            config["StopBits"].get<int>()) /
           baud;
  }
  void quiet(unsigned g, double deadline, bool reject = false) {
    for (;;) {
      double until = clock_now() + interval(true);
      try {
        read(256, g, std::min(until, deadline));
      } catch (const Error &e) {
        if (e.code == 6003 && until <= deadline)
          return;
        throw;
      }
      if (reject)
        throw Error(6005, "RTU trailing bytes");
    }
  }
  Bytes rtu(const Command &c, unsigned g, double deadline) {
    Bytes b;
    for (;;) {
      double limit = b.empty()
                         ? deadline
                         : std::min(deadline, clock_now() + interval(false));
      Bytes next;
      try {
        next = read(256 - b.size(), g, limit);
      } catch (const Error &e) {
        if (e.code == 6003 && !b.empty())
          throw Error(6005, "RTU frame gap");
        throw;
      }
      b.insert(b.end(), next.begin(), next.end());
      if (b.size() >= 2 && (b[0] != c.unit ||
                            (b[1] != c.function && b[1] != (c.function | 128))))
        throw Error(6005, "RTU identity");
      if (b.size() < 3)
        continue;
      size_t total = (b[1] & 128)
                         ? 5
                         : ((c.function <= 4 || c.function == 23) ? b[2] + 5
                            : c.function == 22                    ? 10
                                                                  : 8);
      if (total > 256 || b.size() > total)
        throw Error(6005, "RTU length");
      if (b.size() == total) {
        quiet(g, deadline, true);
        Bytes payload(b.begin(), b.end() - 2);
        if (crc16(payload) != (b[b.size() - 2] | b.back() << 8))
          throw Error(6005, "RTU CRC");
        return Bytes(b.begin() + 1, b.end() - 2);
      }
    }
  }
  Bytes receive(const Command &c, unsigned g, double deadline) {
    std::string framing = config["Framing"];
    if (framing == "Length16") {
      auto h = exact(2, g, deadline);
      size_t n = (h[0] << 8) | h[1];
      if (n > 512)
        throw Error(6005, "Frame size");
      return exact(n, g, deadline);
    }
    if (framing == "Fixed")
      return exact(config["FixedLength"], g, deadline);
    if (framing == "Raw") {
      size_t maximum = c.length ? c.length : 512;
      if (buffer.empty())
        return read(maximum, g, deadline);
      size_t n = std::min(maximum, buffer.size());
      Bytes b(buffer.begin(), buffer.begin() + n);
      buffer.erase(buffer.begin(), buffer.begin() + n);
      return b;
    }
    Bytes delimiter;
    if (framing == "Delimiter") {
      std::string hex = config["DelimiterHex"];
      for (size_t i = 0; i < hex.size(); i += 2)
        delimiter.push_back(std::stoul(hex.substr(i, 2), nullptr, 16));
    }
    for (;;) {
      if (!delimiter.empty()) {
        auto end = std::search(buffer.begin(), buffer.end(), delimiter.begin(),
                               delimiter.end());
        if (end != buffer.end()) {
          size_t n = end - buffer.begin();
          if (n > 512)
            throw Error(6005, "Frame size");
          Bytes b(buffer.begin(), end);
          buffer.erase(buffer.begin(), end + delimiter.size());
          return b;
        }
      }
      if (buffer.size() >
          (delimiter.empty() ? 512 : 512 + delimiter.size() - 1))
        throw Error(6005, "Frame size");
      try {
        double limit =
            framing == "Idle" && !buffer.empty()
                ? std::min(deadline,
                           clock_now() +
                               config["IdleMilliseconds"].get<double>() / 1000)
                : deadline;
        auto b = read(1, g, limit);
        buffer.insert(buffer.end(), b.begin(), b.end());
      } catch (const Error &e) {
        if (framing == "Idle" && !buffer.empty() && e.code == 6003 &&
            clock_now() < deadline) {
          Bytes b;
          b.swap(buffer);
          return b;
        }
        throw;
      }
    }
  }

public:
  explicit Channel(Json c) : config(std::move(c)) {}
  unsigned epoch() const { return generation; }
  void close(bool reset = true) {
    std::lock_guard guard(lock);
    ++generation;
    fd.reset();
    if (reset)
      poisoned = false;
  }
  std::pair<Bytes, bool> execute(const Command &c, unsigned g) {
    check(g);
    if (c.action == 5) {
      close();
      buffer.clear();
      return {{}, false};
    }
    if (poisoned)
      throw Error(6004, "Explicit close required");
    if (c.length > 512)
      throw Error(6007, "Payload");
    std::string protocol = config["Protocol"];
    bool modbus = protocol == "ModbusTcp" || protocol == "ModbusRtu";
    if (modbus              ? c.action != 1
        : protocol == "Tcp" ? (c.action < 2 || c.action > 4)
                            : (c.action != 3 && c.action != 4))
      throw Error(6007, "Action");
    Bytes pdu;
    if (modbus) {
      pdu = encode(c);
      if (c.unit > (protocol == "ModbusRtu" ? 247 : 255))
        throw Error(6007, "Unit");
      if (protocol == "ModbusRtu" && !c.unit && c.function != 5 &&
          c.function != 6 && c.function != 15 && c.function != 16 &&
          c.function != 22)
        throw Error(6007, "Broadcast");
    } else if (c.action == 3 && !c.length)
      throw Error(6007, "Empty send");
    double deadline =
        clock_now() + config["TimeoutMilliseconds"].get<double>() / 1000;
    bool sent = false,
         write = (modbus &&
                  (c.function == 5 || c.function == 6 || c.function == 15 ||
                   c.function == 16 || c.function == 22 || c.function == 23)) ||
                 c.action == 3;
    try {
      if (resource(g) < 0) {
        if (protocol == "Tcp" && c.action != 2)
          throw Error(6007, "Explicit TCP connect required");
        buffer.clear();
        open(g, deadline);
      }
      if (c.action == 2)
        return {{}, false};
      if (!modbus) {
        if (c.action == 4)
          return {receive(c, g, deadline), false};
        Bytes data(c.data, c.data + c.length);
        std::string framing = config["Framing"];
        if (framing == "Length16") {
          data.insert(data.begin(), uint8_t(c.length));
          data.insert(data.begin(), uint8_t(c.length >> 8));
        } else if (framing == "Delimiter") {
          std::string hex = config["DelimiterHex"];
          for (size_t i = 0; i < hex.size(); i += 2)
            data.push_back(std::stoul(hex.substr(i, 2), nullptr, 16));
        } else if (framing == "Fixed" &&
                   data.size() != config["FixedLength"].get<size_t>())
          throw Error(6007, "Fixed send size");
        send(data, g, deadline, sent);
        return {{}, false};
      }
      Bytes response;
      if (protocol == "ModbusTcp") {
        transaction = transaction % 65535 + 1;
        Bytes b{uint8_t(transaction >> 8),
                uint8_t(transaction),
                0,
                0,
                uint8_t((pdu.size() + 1) >> 8),
                uint8_t(pdu.size() + 1),
                uint8_t(c.unit)};
        b.insert(b.end(), pdu.begin(), pdu.end());
        send(b, g, deadline, sent);
        auto h = exact(7, g, deadline);
        size_t n = (h[4] << 8) | h[5];
        if (((h[0] << 8) | h[1]) != transaction || h[2] || h[3] ||
            h[6] != c.unit || n < 2 || n > 254)
          throw Error(6005, "MBAP");
        response = exact(n - 1, g, deadline);
      } else {
        quiet(g, deadline);
        Bytes b{uint8_t(c.unit)};
        b.insert(b.end(), pdu.begin(), pdu.end());
        auto crc = crc16(b);
        b.push_back(crc);
        b.push_back(crc >> 8);
        send(b, g, deadline, sent);
        if (!c.unit)
          return {{}, false};
        response = rtu(c, g, deadline);
      }
      return {decode(c, response), true};
    } catch (const Error &e) {
      if (e.code >= 6201 && e.code <= 6455)
        throw;
      fail(g);
      if (write && sent)
        throw Error(6008, "Write outcome unknown");
      throw;
    } catch (...) {
      fail(g);
      throw Error(write && sent ? 6008 : 6004, "Transport failure");
    }
  }
};
} // namespace stcommunication

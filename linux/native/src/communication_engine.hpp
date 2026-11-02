// SPDX-License-Identifier: MIT
#pragma once
#include "communication_channel.hpp"
#include <array>
#include <cctype>
#include <cstring>
#include <set>
namespace stcommunication {
inline std::pair<int, std::vector<Json>> validate(const Json &m) {
  auto fail = []() {
    throw std::runtime_error("Invalid communication manifest");
  };
  if (!m.is_object() || m.size() != 4 ||
      !m.at("SchemaVersion").is_number_integer() ||
      m.at("SchemaVersion") != 1 ||
      m.at("InterfaceVersion") != "workbench-communication-v1")
    fail();
  auto number = [&](const Json &o, const char *k, int low, int high) {
    const auto &v = o.at(k);
    if (!v.is_number_integer() || v.is_boolean())
      fail();
    if (v.is_number_unsigned()) {
      auto n = v.get<uint64_t>();
      if (n < static_cast<uint64_t>(low) || n > static_cast<uint64_t>(high))
        fail();
      return static_cast<int>(n);
    }
    auto n = v.get<int64_t>();
    if (n < low || n > high)
      fail();
    return static_cast<int>(n);
  };
  int period = number(m, "PlcIntervalMilliseconds", 1, 1000);
  const auto &rows = m.at("Channels");
  if (!rows.is_array() || rows.empty() || rows.size() > 16)
    fail();
  std::vector<Json> configs;
  std::set<std::string> names, devices;
  std::string previous;
  const std::set<std::string> keys = {"Name",
                                      "Protocol",
                                      "Host",
                                      "Port",
                                      "Device",
                                      "BaudRate",
                                      "DataBits",
                                      "Parity",
                                      "StopBits",
                                      "Physical",
                                      "Direction",
                                      "BeforeSendMicroseconds",
                                      "AfterSendMicroseconds",
                                      "TimeoutMilliseconds",
                                      "Framing",
                                      "DelimiterHex",
                                      "FixedLength",
                                      "IdleMilliseconds"};
  for (size_t i = 0; i < rows.size(); ++i) {
    const auto &r = rows[i];
    if (!r.is_object() || r.size() != 2 ||
        number(r, "Index", 1, 16) != static_cast<int>(i + 1))
      fail();
    auto c = r.at("Configuration");
    std::set<std::string> actual;
    for (auto it = c.begin(); it != c.end(); ++it)
      actual.insert(it.key());
    if (actual != keys)
      fail();
    std::string name = c.at("Name"), lower = name;
    if (name.empty() || name.size() > 64 ||
        !(std::isalpha(static_cast<unsigned char>(name[0])) || name[0] == '_'))
      fail();
    for (auto &ch : lower) {
      if (static_cast<unsigned char>(ch) > 127 ||
          !(std::isalnum(static_cast<unsigned char>(ch)) || ch == '_'))
        fail();
      ch = std::tolower(static_cast<unsigned char>(ch));
    }
    if ((i && name < previous) || !names.insert(lower).second)
      fail();
    previous = name;
    std::string p = c.at("Protocol"), framing = c.at("Framing");
    if (p != "Tcp" && p != "Serial" && p != "ModbusTcp" && p != "ModbusRtu")
      fail();
    if ((p == "ModbusTcp" || p == "ModbusRtu") && framing != "Raw")
      fail();
    number(c, "TimeoutMilliseconds", 10, 10000);
    if (p == "Tcp" || p == "ModbusTcp") {
      std::string h = c.at("Host");
      std::array<unsigned char, 16> b{};
      if (inet_pton(AF_INET, h.c_str(), b.data()) != 1 &&
          inet_pton(AF_INET6, h.c_str(), b.data()) != 1)
        fail();
      number(c, "Port", 1, 65535);
    } else {
      std::string d = c.at("Device");
      if (!d.starts_with("/dev/") || d.size() > 133 ||
          !devices.insert(d).second)
        fail();
      size_t start = 0;
      for (size_t n = 0; n <= d.size(); ++n) {
        if (n == d.size() || d[n] == '/') {
          auto component = d.substr(start, n - start);
          if (component == "." || component == "..")
            fail();
          start = n + 1;
        } else if (static_cast<unsigned char>(d[n]) > 127 ||
                   !(std::isalnum(static_cast<unsigned char>(d[n])) ||
                     d[n] == '_' || d[n] == '.' || d[n] == '-'))
          fail();
      }
      int baud = number(c, "BaudRate", 1200, 230400);
      if (!std::set<int>{1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200,
                         230400}
               .count(baud))
        fail();
      int bits = number(c, "DataBits", 7, 8),
          stop = number(c, "StopBits", 1, 2);
      std::string parity = c.at("Parity"), physical = c.at("Physical"),
                  direction = c.at("Direction");
      if (parity != "None" && parity != "Even" && parity != "Odd")
        fail();
      if (p == "ModbusRtu" && (bits != 8 || (parity == "None" && stop != 2)))
        fail();
      if (physical != "RS232" && physical != "RS485")
        fail();
      if (direction != "Automatic" && direction != "KernelRts")
        fail();
      if (physical == "RS232" && direction == "KernelRts")
        fail();
      number(c, "BeforeSendMicroseconds", 0, 100000);
      number(c, "AfterSendMicroseconds", 0, 100000);
    }
    if (framing != "Raw" && framing != "Fixed" && framing != "Delimiter" &&
        framing != "Length16" && framing != "Idle")
      fail();
    std::string hex = c.at("DelimiterHex");
    if (hex.size() < 2 || hex.size() > 16 || hex.size() % 2 ||
        hex.find_first_not_of("0123456789abcdefABCDEF") != std::string::npos)
      fail();
    number(c, "FixedLength", 1, 512);
    number(c, "IdleMilliseconds", 1, 1000);
    configs.push_back(c);
  }
  return {period, configs};
}
struct Feedback {
  uint64_t request = 0, busy = 0, done = 0, error = 0, unknown = 0, ack = 0,
           length = 0;
  uint8_t data[512]{};
};
static_assert(sizeof(Feedback) == 568);
class Engine {
  std::vector<std::unique_ptr<Channel>> channels;
  std::array<Feedback, 16> feedback{};
  std::array<uint64_t, 16> highest{}, generations{};
  std::array<std::thread, 16> threads;
  std::array<std::atomic<bool>, 16> active{};
  std::mutex mutex;
  double last = 0, timeout;
  void close_locked() {
    healthy = false;
    for (size_t i = 0; i < channels.size(); ++i) {
      ++generations[i];
      channels[i]->close();
      if (feedback[i].busy) {
        auto request = feedback[i].request;
        feedback[i] = {};
        feedback[i].request = request;
        feedback[i].error = 6002;
        feedback[i].unknown = 1;
      }
    }
  }

public:
  uint64_t scan = 0;
  bool healthy = false;
  Engine(const std::vector<Json> &configs, int period)
      : timeout(std::max(.25, 3 * period / 1000.)) {
    for (auto &c : configs)
      channels.push_back(std::make_unique<Channel>(c));
  }
  ~Engine() {
    close();
    for (auto &t : threads)
      if (t.joinable())
        t.join();
  }
  void close() {
    std::lock_guard guard(mutex);
    close_locked();
  }
  void tick() {
    std::lock_guard guard(mutex);
    if (healthy && clock_now() - last > timeout)
      close_locked();
  }
  Bytes image(const Bytes &data) {
    if (data.size() != 16 + 16 * sizeof(Command))
      throw std::runtime_error("Image size");
    uint64_t s, flags;
    memcpy(&s, data.data(), 8);
    memcpy(&flags, data.data() + 8, 8);
    std::lock_guard guard(mutex);
    if (healthy && clock_now() - last > timeout)
      close_locked();
    bool fresh = flags == 1 && s > scan;
    if (flags != 1)
      close_locked();
    if (fresh) {
      scan = s;
      last = clock_now();
      healthy = true;
    }
    if (healthy && fresh)
      for (size_t i = 0; i < 16; ++i) {
        Command c{};
        memcpy(&c, data.data() + 16 + i * sizeof(c), sizeof(c));
        if (!c.request || c.request == highest[i])
          continue;
        auto &f = feedback[i];
        if (c.request < highest[i]) {
          if (!f.busy) {
            f = {};
            f.request = c.request;
            f.error = 6009;
          }
          continue;
        }
        highest[i] = c.request;
        auto set = [&](unsigned error) {
          f = {};
          f.request = c.request;
          f.error = error;
        };
        if (i >= channels.size()) {
          set(6001);
          continue;
        }
        if (c.action == 5) {
          ++generations[i];
          channels[i]->close();
          set(0);
          f.done = 1;
          continue;
        }
        if (f.busy || active[i]) {
          set(6006);
          continue;
        }
        if (threads[i].joinable())
          threads[i].join();
        auto generation = ++generations[i];
        auto epoch = channels[i]->epoch();
        set(0);
        f.busy = 1;
        active[i] = true;
        threads[i] = std::thread([this, i, c, generation, epoch] {
          Feedback result{};
          result.request = c.request;
          try {
            auto [data, ack] = channels[i]->execute(c, epoch);
            result.done = 1;
            result.ack = ack;
            result.length = data.size();
            std::copy(data.begin(), data.end(), result.data);
          } catch (const Error &e) {
            result.error = e.code;
            result.unknown = e.code == 6008;
          } catch (...) {
            result.error = 6004;
            result.unknown = 1;
          }
          std::lock_guard guard(mutex);
          if (generations[i] == generation && healthy)
            feedback[i] = result;
          active[i] = false;
        });
      }
    Bytes out(sizeof(feedback));
    memcpy(out.data(), feedback.data(), out.size());
    return out;
  }
};
} // namespace stcommunication

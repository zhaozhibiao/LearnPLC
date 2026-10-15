#include "motion_bridge.hpp"
#include <chrono>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

namespace workbench_motion {
namespace {
Mailbox<Command> commands;
Mailbox<Feedback> feedbacks;
std::atomic<bool> running{false};
std::thread worker;
std::mutex lifecycle;

bool transfer(int socket, void* data, std::size_t size, bool writing) {
    auto* p = static_cast<char*>(data);
    while (size) {
        auto count = writing ? ::send(socket, p, size, MSG_NOSIGNAL) : ::recv(socket, p, size, 0);
        if (count <= 0) return false;
        p += count;
        size -= static_cast<std::size_t>(count);
    }
    return true;
}

void exchange(std::string endpoint) {
    int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    std::memcpy(address.sun_path, endpoint.c_str(), endpoint.size() + 1);
    timeval timeout{0, 50000};
    if (fd >= 0) {
        ::setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
        ::setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
        if (::connect(fd, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0) {
            ::close(fd);
            fd = -1;
        }
    }
    Command latest{};
    while (running.load(std::memory_order_acquire)) {
        Command next;
        bool stop_seen = false;
        // Bound drain work even if PLC scans much faster than transport.
        for (unsigned i = 0; i < 16 && commands.pop(next); ++i) {
            stop_seen = stop_seen || (next.flags & 2U);
            latest = next;
        }
        if (stop_seen) latest.flags |= 2U; // a one-scan Stop pulse must reach the owner
        if (latest.scan) {
            Feedback result{latest.request, 0, 0, transport_error, 0.};
            if (fd >= 0 && (!transfer(fd, &latest, sizeof(latest), true) ||
                           !transfer(fd, &result, sizeof(result), false))) {
                ::close(fd);
                fd = -1;
                result = Feedback{latest.request, 0, 0, transport_error, 0.};
            }
            feedbacks.push(result); // full feedback queue drops old publication; next exchange retries
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    if (fd >= 0) ::close(fd); // worker disconnect cancels its control lease
}
}

bool start(const char* endpoint) {
    std::lock_guard<std::mutex> guard(lifecycle);
    if (running || !endpoint || endpoint[0] != '/' ||
        std::strlen(endpoint) >= sizeof(sockaddr_un::sun_path)) return false;
    const std::uint16_t endian = 1;
    if (*reinterpret_cast<const unsigned char*>(&endian) != 1) return false;
    Command c; Feedback f;
    while (commands.pop(c)) {}
    while (feedbacks.pop(f)) {}
    running.store(true, std::memory_order_release);
    worker = std::thread(exchange, std::string(endpoint));
    return true;
}

void stop() {
    std::lock_guard<std::mutex> guard(lifecycle);
    running.store(false, std::memory_order_release);
    if (worker.joinable()) worker.join();
}
bool submit(const Command& command) noexcept {
    return running.load(std::memory_order_acquire) && commands.push(command);
}
bool poll(Feedback& result) noexcept { return feedbacks.pop(result); }
}

extern "C" bool workbench_motion_start(const char* endpoint) { return workbench_motion::start(endpoint); }
extern "C" void workbench_motion_stop() { workbench_motion::stop(); }

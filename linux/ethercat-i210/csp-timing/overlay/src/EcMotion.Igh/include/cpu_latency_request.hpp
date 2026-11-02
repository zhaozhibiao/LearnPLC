#pragma once
#include <cerrno>
#include <cstdint>
#include <cstring>
#include <fcntl.h>
#include <stdexcept>
#include <string>
#include <unistd.h>

namespace ecmotion {
// The kernel applies PM QoS only while this descriptor remains open.
class CpuLatencyRequest final {
public:
    explicit CpuLatencyRequest(const char* path="/dev/cpu_dma_latency") {
        fd_=::open(path,O_RDWR|O_CLOEXEC|O_NOFOLLOW);
        if(fd_<0) throw std::runtime_error("Cannot acquire CPU latency request: "+std::string(std::strerror(errno)));
        const std::int32_t zero=0;
        ssize_t count;
        do {count=::write(fd_,&zero,sizeof(zero));} while(count<0 && errno==EINTR);
        if(count!=static_cast<ssize_t>(sizeof(zero))) {
            ::close(fd_);fd_=-1;
            throw std::runtime_error("Cannot apply zero CPU wakeup latency request");
        }
    }
    ~CpuLatencyRequest() {if(fd_>=0) ::close(fd_);}
    CpuLatencyRequest(const CpuLatencyRequest&)=delete;
    CpuLatencyRequest& operator=(const CpuLatencyRequest&)=delete;
    [[nodiscard]] int descriptor() const noexcept {return fd_;}
private:
    int fd_{-1};
};
}

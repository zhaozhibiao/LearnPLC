#include "cpu_latency_request.hpp"
#include <fcntl.h>
#include <unistd.h>
#include <cstdint>
#include <stdexcept>
int main() {
    char path[]="/tmp/ecmotion-latency-test-XXXXXX";
    int file=mkstemp(path); if(file<0) return 1;
    int descriptor=-1;
    {
        ecmotion::CpuLatencyRequest request(path);descriptor=request.descriptor();
        std::int32_t value=-1;
        if(pread(file,&value,sizeof(value),0)!=sizeof(value) || value!=0) return 2;
        if(fcntl(descriptor,F_GETFD)<0) return 3;
    }
    if(fcntl(descriptor,F_GETFD)>=0) return 4;
    close(file);unlink(path);
    try {ecmotion::CpuLatencyRequest missing("/definitely-missing-ecmotion/cpu_dma_latency");return 5;}
    catch(const std::runtime_error&) {}
    return 0;
}

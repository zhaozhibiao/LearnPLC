#include "generated.hpp"
#include "motion_bridge.hpp"
#include "runtime_lifecycle.inc"
#include <cassert>
#include <chrono>
#include <iostream>
#include <thread>
#include <cstring>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

int main(int argc, char** argv) {
    assert(argc == 2);
    using namespace strucpp;
    using namespace workbench_motion;
    Mailbox<Command, 3> mailbox;
    Command c{}; c.request = 42;
    assert(mailbox.push(c));
    assert(mailbox.push(c));
    assert(!mailbox.push(c));
    assert(mailbox.pop(c) && c.request == 42);
    assert(mailbox.pop(c));
    assert(!mailbox.pop(c));
    Configuration_CONFIG0 configuration;
    auto& program = configuration.INSTANCE0;
    auto scan = [&] {
        program.run();
        workbench_motion_after_scan(true);
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    };
    assert(::setenv("STWORKBENCH_MOTION_BACKEND", "VirtualContinuous", 1) == 0);
    assert(::setenv("STWORKBENCH_MOTION_SOCKET", argv[1], 1) == 0);
    workbench_motion_bootstrap(); // mirrors candidate get_config lifecycle
    assert(!workbench_motion_start(argv[1])); // lifecycle ownership cannot start twice
    for (int i = 0; i < 3; ++i) scan();
    program.EXECUTE = true;
    scan();
    assert(program.PATH.REQUESTID == 1);
    // A second socket client cannot seize the running virtual axis group.
    int second = ::socket(AF_UNIX, SOCK_STREAM, 0);
    sockaddr_un address{}; address.sun_family = AF_UNIX;
    std::strncpy(address.sun_path, argv[1], sizeof(address.sun_path) - 1);
    assert(::connect(second, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0);
    Command intrusion{}; intrusion.request = 88; intrusion.path = 7;
    intrusion.version = 1; intrusion.hash.fill('a'); intrusion.scan = 1; intrusion.flags = 1;
    assert(::send(second, &intrusion, sizeof(intrusion), MSG_NOSIGNAL) == sizeof(intrusion));
    Feedback denied{};
    assert(::recv(second, &denied, sizeof(denied), MSG_WAITALL) == sizeof(denied));
    assert(denied.request == 88 && denied.error == 1004);
    ::close(second);
    for (int i = 0; i < 160 && !program.PATH.DONE && !program.PATH.ERROR; ++i) scan();
    assert(program.PATH.DONE && !program.PATH.BUSY && !program.PATH.ERROR);
    assert(program.PATH.PROGRESS == 1.);
    assert(MB_REQUESTID == 1); // held Execute never resubmits
    std::cout << "compiled ST execution 1 completed, progress=" << double(program.PATH.PROGRESS) << '\n';
    program.EXECUTE = false; scan();
    program.EXECUTE = true; scan();
    assert(program.PATH.REQUESTID == 2);
    program.STOP = true;
    scan();
    program.STOP = false; // one-scan pulse, followed by another inactive FB
    assert(MB_STOP);
    for (int i = 0; i < 15 && !program.PATH.ERROR; ++i) scan();
    assert(program.PATH.ERROR && program.PATH.ERRORID == 1003);
    assert(!program.PATH.DONE);
    program.STOP = false;
    program.EXECUTE = false; scan();
    program.EXECUTE = true; scan();
    for (int i = 0; i < 3; ++i) scan();
    assert(program.PATH.REQUESTID == 3 && program.PATH.BUSY);
    // Transport continues, but frozen PLC scan cannot renew control permission.
    std::this_thread::sleep_for(std::chrono::milliseconds(350));
    for (int i = 0; i < 5 && !program.PATH.ERROR; ++i) scan();
    assert(program.PATH.ERROR && program.PATH.ERRORID == 1002);
    assert(!program.PATH.DONE);
    workbench_motion_after_scan(false);
    workbench_motion_stop();
    workbench_motion_after_scan(false);
    assert(MB_ERRORID == 1009 && !MB_DONE);
    std::cout << "compiled ST stop, scan watchdog and bounded mailbox passed\n";
}

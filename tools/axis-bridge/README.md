# Basic axis ST bridge

This MIT-licensed bridge provides nine canonical scoped MC_ blocks and the legacy
`MC_ReadAxisStatus` alias for `MC_ReadStatus` from the approved
axis design. It is not a complete PLCopen implementation. Existing continuous
path bridge files and exported names remain independent.

`axis_control.st` defines AXIS_REF as UINT. Project-generated `AXIS_<Name>` constants
bind 1..64 to the immutable axis manifest. `AB_Command` is an internal arbiter;
user programs must only use the public MC_ blocks and generated constants.

Reserved compiler-owned globals: AB_ScanCounter (ULINT); fixed ARRAY[1..64]
AB_Request, AB_Kind, AB_FeedbackRequest, AB_State, AB_MotionId (ULINT),
AB_Position, AB_Velocity, AB_ActualPosition, AB_ActualVelocity (LREAL),
AB_StopHeld, AB_Busy, AB_Done, AB_Aborted, AB_Enabled, AB_Homed (BOOL),
AB_Error (UDINT). Generated C++ names are uppercase. Native shim accesses the
compiler's typed arrays, never guessed byte offsets.

CommandPacket is exactly 2576 bytes; FeedbackPacket is exactly 5632 bytes.
V1 does not carry dynamic A/D/J inputs. The separate v2 bridge carries those.
The 64 command slots contain request/kind/position/velocity/flags (40 bytes each). The packet
header is actual successful PLC scan counter and flags bit 0 = scan succeeded.
Slot flag bit 0 = Stop held. Feedback includes request/busy/done/error/aborted/
enabled/homed/state/motion_id/position/velocity. Host little endian is required.
The native bridge connects only to the isolated worker binary endpoint, never
directly to the controller JSON socket. The worker owns protocol validation,
manifest binding, capabilities, limits, watchdog and control leases.

Ordinary Execute rising edges reject busy axes (2006). Stop and Disable preempt
only their own axis; previous owners report CommandAborted. Stop stays locked
while its owner holds Execute; Disable releases that lock. New feedback cannot
manufacture a completion: request IDs must match, and completion also requires
an actual terminal axis state. Absolute moves require a nonzero motion_id;
the worker must prove it equals the device command's ID. Velocity moves remain
active and InVelocity compares actual velocity on the accepted axis with the velocity latched when its request was accepted. Live Axis/Velocity input changes while Execute is held do not retarget that comparison.
Finite values, index range and request counter exhaustion are guarded.

Busy denotes an accepted pending operation; Active requires matching busy
feedback. Terminal Done/Error/CommandAborted stay latched until Execute is false;
an Execute released while busy does not cancel and leaves the terminal result
visible for at least one call. Retriggering an ordinary busy instance does not
retarget it. A completed held Stop retains Busy/Active and public Stopping until
Execute is released. MC_Power is level controlled, reports live enable/fault,
and defers a new Enable edge during Disable only until successful confirmation;
a failed transition never retries automatically.

Read blocks expose Valid/Busy/Error/ErrorID independently of command success:
fresh ErrorStop and rejected-command feedback remain readable. Failed workers
publish state 0. The shim also masks stale state/enabled/homed from older workers
on infrastructure errors 2002/2003/2004/2008, without changing the binary layout.
No position/velocity value is usable when Valid is false. Internal AB_ details
are excluded from public editor completion. The short business sequence example
is `examples/standard-axis-interfaces/StandardInterfaces.stproj`.

Compile `axis_bridge.cpp` and `generated_axis_scan_shim.cpp` alongside generated
ST C++; include `axis_lifecycle.inc` exactly once in the runtime entry. Call
`workbench_axis_bootstrap()` during get_config/loading outside cyclic execution.
`workbench_axis_start`, `workbench_axis_stop`, `workbench_axis_after_scan` and
bootstrap all use distinct axis export names. The generated program run must
have a RAII guard invoking after_scan with success only when exception count
has not increased. The fixture compiler demonstrates that insertion. Exceptions
publish a failed scan without incrementing AB_ScanCounter.

Runtime must explicitly set STWORKBENCH_AXIS_BACKEND to EcMotion or
EcMotionSimulator, and STWORKBENCH_AXIS_SOCKET to a private absolute endpoint.
The socket and its immediate directory must be owned by the process UID and
have no group/other permissions. Symlink sockets/directories and traversal
components are rejected. Peer UID is verified after connecting. Coordinator
must additionally bind private worker identity/capabilities/manifest before
activating any candidate; native start by itself does not establish readiness.

The scan uses bounded fixed copies and lock-free SPSC queues, with no heap,
mutex, socket or filesystem operations. Each mailbox has 256 fixed slots (255 usable), occupying about 2 MiB in total outside the scan stack. The shim drains at most 32 feedback packets per scan. All packets are exchanged in order;
Enable/Disable/Stop pulses are never reduced to the latest command. Command or
feedback queue overflow is terminal 2004, causing disconnect and worker-owned
controlled stop/release. A failed transport never reconnects or replays requests.
Each nonblocking send/receive frame has one absolute 200ms deadline, allowing
native Home AppliedReceipt waits up to 100ms plus processing margin. Partial
bytes do not reset that deadline. Timeout, EOF or malformed framing fails closed
with 2004 and disconnects; no command replay follows. Connect has a separate
50ms bound. Teardown can wait for at most the outstanding bounded frame poll.
Worker must send exactly one feedback image in response to each queued scan,
without unsolicited feedback frames. Background polling cadence is independent
of the PLC scan cadence; idle controller polls must not renew scan ownership.
The lifecycle thread joins on stop. Quiesce cyclic calls before teardown/restart,
and destroy the lifetime before dlclose. Do not unload a running scan.

Verification: `node tools/axis-bridge/compile_fixture.mjs` uses pinned STruC++
0.7.0. `verify-linux.sh` builds and runs bounded native tests without any remote
controller. `test_scan.cpp` tests generated real ST, with `test_stub.cpp` only
replacing IPC for deterministic feedback and exception assertions.
`verify_public_interfaces.py <staged-fixture-directory>` compiles the generated
blocks and runs 19 public interface scenarios, including legacy worker failures.
`examples/standard-axis-interfaces/verify_sequence.py` additionally runs nine
offline business sequence scenarios; neither test sends device commands.
`test_transport.cpp` uses a fresh private local socket to test ordered power/stop
packets with 100ms delayed replies, duplicate start, explicit overflow, 120 scans queued at 1ms while a 100ms receipt is pending, a byte-trickle whole-frame timeout and joined teardown. Linux is the
production transport target; local Cygwin GCC 11.4 can also compile these fixtures.
No hardware control, download, activation or external network is part of tests.




# Axis dynamics v2 bridge

This separate `workbench-axis-v2` bridge exports `workbench_axis_v2()` returning 2
and the usual axis lifecycle hooks. V1 remains a 2576-byte command image.
V2 has a 16-byte `<QQ>` scan header and 64 `<QQdddddQ>` command slots,
4112 command bytes and 6656 feedback bytes. Each 104-byte feedback slot is
`<9Q2d2Q>`: existing state fields followed by rejected_request/rejected_error.
Primary request/state always belong to the last accepted owner, so a rejected
new request cannot erase active motion tracking. Rejected requests have separate
receipts and cannot resubmit on repeated frames. Shim arrays include acceleration,
deceleration and jerk and are copied by value on every scan. The ST arbiter
owns rising-edge sampling and keeps accepted request parameters immutable.

The worker is `linux/axis/axis_worker_v2.py`. Production uses the dedicated
`/home/a/.local/share/st-workbench/axis-v2/axis.sock` endpoint and
`stworkbench-axis-v2.service`. Bootstrap reads `STWORKBENCH_AXIS_V2_SOCKET`
and the explicit `STWORKBENCH_AXIS_BACKEND` value. No v1 fallback or packet-length
detection exists. Filesystem ownership, private modes and peer UID are checked.

Before any command, worker sends a 24-byte `<8sIIII>` greeting containing
`AXISV2\0\0`, version 2, command bytes 4112, feedback bytes 6656, reserved 0.
Bridge verifies it and echoes the exact greeting. Worker verifies the echo
before parsing commands. Each native transfer has a whole-frame 200ms deadline;
worker also bounds handshake completion at 200ms. Wrong versions, EOF and partial
frames cannot produce an accepted command. Queues preserve order and overflow
fails closed. Scan execution performs fixed copies and lock-free mailbox access;
all socket operations and waits run outside the scan.

Manifest requires `InterfaceVersion`, `CommandBytes`, `FeedbackBytes` and
`RequiredRuntimeCapabilities`. Native Hello `runtimeCapabilities` must contain
all requirements, including `workbench_axis_move_profile_v2`. Published metadata
includes the same sizes, requirements and `NativeRuntimeCapabilities`.
Each axis requires finite positive MaximumAcceleration/MaximumDeceleration and
explicit finite nonnegative MaximumJerk. Zero request values inherit these
engineering values; negatives, nonfinite values and values above maxima reject.
MoveAbsolute sends explicit `motionProfileVersion: 2` and effective A/D/J.
All v2 Jog/ordinary Stop requests resolve defaults, including zero inputs, and require `workbench_axis_velocity_profile_v2`;
a v2 program containing Jog/Stop requires that capability even when those calls omit dynamics. Protective shutdown continues
to use the existing immediate safety path rather than an ordinary profile.

Verification: run `python -m unittest discover -s linux/axis -p 'test_*.py'`.
On Linux, compile `axis_bridge.cpp` with `test_transport.cpp` using C++17/pthreads.
Compile the generated ST fixture with `test_scan.cpp` and the shim. Native Linux
transport tests require Unix sockets; Windows Python discovery skips service
integration. These checks do not authorize physical commissioning.

## Preparing the stopped user service

`python3 linux/axis/install_axis_v2.py --candidate <verified-version-directory>`
prepares files as unprivileged user a. It validates the existing compiled
candidate/inventory, v2 manifest, and exact packaged worker identity, then stages
worker/manifest hashes in private `axis-v2/prepared.json`, the fixed user unit and
an environment-only Runtime drop-in. Configuration JSON updates preserve unknown
fields and secrets and retain a private backup. Existing v1 resources are untouched.
The v2 service must be stopped (a first-install missing unit is allowed); foreign
units, symlinks, nonprivate configuration or writable installation parents reject.

The command reloads systemd unit definitions after successful preparation; this
does not change the running Runtime process environment. A controlled coordinator
management restart is required to load the new environment.

Preparation never connects to native core, starts a worker,
restarts Runtime, enables a unit or activates a PLC. It is possible against an
old core because it makes no readiness claim. The fixed unit has no automatic
start target and no Restart policy. A subsequent explicitly authorized management
reload/restart must load `STWORKBENCH_AXIS_V2_SOCKET`; hardware apply uses the
existing fixed-service configuration pipeline and must verify native profile
capabilities before starting this service. An unsupported native core must never
be used to start it. No installer option accepts an executable or service name.

## Real ST plus shim fixture

Generate real ST C++ with `node tools/axis-bridge-v2/compile_fixture.mjs`.
Then on Linux compile `build/generated.cpp`, `generated_axis_scan_shim.cpp`,
`test_stub.cpp` and `test_scan.cpp` with C++20, pthreads, and include directories
`tools/axis-bridge-v2`, `tools/axis-bridge-v2/build` and
`.tools/strucpp-0.7.0/strucpp/runtime/include`. Run the resulting fixture binary.
Do not link `axis_bridge.cpp` into this fixture: the stub replaces only IPC and
lifecycle calls. The actual generated public MC_Stop blocks and production shim
are linked together. Assertions cover accepted Stop owner 1, rejected request 2,
owner release replay 1 with allocator highwater 2, retained rejection receipt 2,
next request 3, all 64 command copies and malformed/future feedback rejection.
The separate Unix transport fixture tests the actual background bridge.

Runtime activation/recovery explicitly restores the active v2 service after
Runtime start because the worker is PartOf Runtime and has no automatic Wants
relationship. Before either service starts, the coordinator verifies the active
compiled candidate, current native capabilities, channel/environment, and installed
worker/manifest identity. It then starts Runtime, starts the fixed v2 service,
waits for bridge readiness, and confirms HTTP reports STOPPED. This management
recovery never requests PLC START. Existing v1 recovery keeps its original flow.

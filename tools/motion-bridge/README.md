# Continuous path scan bridge — isolated virtual backend

This implementation supports **VirtualContinuous** only. It neither opens a hardware
device nor connects to the existing EcMotion controller. Physical Continuous remains
unsupported; using an `EcMotion` backend in this worker is rejected.

The immutable `PathResource` identity and exclusive controller lease follow the source
design in `EcMotion.Protocol/ProtocolMessages.cs`, `path_resource.hpp` and
`realtime_exchange.hpp`. The fixed scan mailbox keeps the same realtime/non-realtime
ownership split. The virtual adapter does not claim that the original ExactStop
`RealtimePath` executor has gained continuous hardware support.

## Required integration

1. Include `continuous_path.st` in compiler additional sources for motion projects.
   `MC_ExecuteContinuousPath` binds `PathId`, `Version` and the full 64-character
   `ContentHash`. It publishes one request on Execute's rising edge. One configured
   axis group command image is supported; conflicting FB calls get ErrorID 1008.
2. Compile `motion_bridge.cpp` and `generated_scan_shim.cpp` into the candidate with
   C++17 and `-pthread`. The shim includes the candidate's **generated.hpp**; direct
   compiler-generated typed globals provide the authoritative layout.
3. Lifecycle code calls `workbench_motion_start(private_socket)` outside the scan.
   Call `workbench_motion_after_scan(true)` exactly once after each actual ST scan.
   PLC stop calls `workbench_motion_after_scan(false)`. Program replacement/shutdown
   calls `workbench_motion_stop()` outside the scan **before** unloading the candidate.
   A missing lifecycle start returns transport error and never fabricates completion.
   `runtime_lifecycle.inc` provides a bootstrap for candidate `strucpp_get_config()`
   and teardown before unload. It requires `STWORKBENCH_MOTION_BACKEND=VirtualContinuous`
   and `STWORKBENCH_MOTION_SOCKET` pointing at an explicitly verified private socket.
4. Launch `linux/motion/virtual_worker.py --manifest paths/motion-manifest.json
   --socket /private/test-directory/virtual.sock` explicitly in an isolated directory.
   It refuses to replace an existing socket. No auto-start or service changes occur.

The worker atomically publishes `socket.capabilities.json` as a mode 0600 regular
file containing its PID, VirtualContinuous capability, exact manifest SHA-256 and
immutable path identities. The activation coordinator must verify owner, live PID,
socket type and matching candidate manifest before permitting motion activation.
Clean shutdown removes only the worker's own socket and readiness file. Existing
socket or metadata ownership is never overwritten.

The manifest is `{"SchemaVersion":1,"Backend":"VirtualContinuous","Paths":[
{"Asset":<MotionPathAsset>,"Trajectory":<prepared sampled trajectory>} ]}`.
The package producer must validate Core asset hashes and geometric planning before
sealing the bundle. The worker checks immutable request identity, collisions, axis
mapping shape, finite samples, strict timestamps and bounded preload budgets. It
does not independently reproduce .NET's JSON hash serialization or certify physical
constraints. The manifest is a trusted candidate artifact, never a direct DXF input.

The worker consumes preplanned Position/Velocity/Acceleration samples on its own
virtual tick. It returns Done only after consuming the final sample; desktop code
cannot set Done. This is a virtual timing test, without hard realtime or hardware
tracking guarantees. A missed scheduling interval can consume multiple virtual
samples, so it must not be used as a physical servo output loop.

## Stop, ownership and feedback

Only one local socket connection owns the virtual axis group. Other connections
receive ownership refusal without changing the active execution. The lease watchdog
renews only when **PLC scan counter advances**, never from the background socket
thread's repeated packet. Stop, PLC stop, stale scans and disconnect cancel Busy;
they cannot turn into Done. Connection loss does not automatically resume execution.
Native scan work has bounded queue operations and fixed copies, with no socket calls,
locks, parsing, allocation or thread creation.

Feedback is exposed as FB `Busy`, `Done`, `Error`, `ErrorID`, `Progress` (0–1) and
`RequestId` through the normal authoritative debugger table. The same values should
drive the path page; no separate desktop progress timer is needed.

| ErrorID | Meaning |
|---|---|
|1001|Asset identity/version/hash mismatch|
|1002|PLC scans stopped advancing|
|1003|Explicit Stop or PLC stopped|
|1004|Another owner holds control|
|1005|Owner disconnected|
|1006|Virtual worker fault|
|1007|Conflicting/stale request identity|
|1008|Axis group already has a request|
|1009|Bridge unavailable or bounded transport/mailbox failure|

## Verification

Compile the real fixture using STruC++ 0.7.0:

```powershell
.tools/strucpp-0.7.0/strucpp/strucpp.exe tools/motion-bridge/scan_fixture.st tools/motion-bridge/continuous_path.st -o artifacts/motion-bridge-verify/generated.cpp
python -m unittest discover -s linux/motion -p test_virtual_worker.py
```

Copy generated.cpp/generated.hpp, this bridge directory, the compiler runtime include
directory and linux/motion into a private Linux test directory. Then:

```sh
python3 linux/motion/verify_isolated.py --generated artifacts/motion-bridge-verify --runtime-include runtime/include --bridge tools/motion-bridge
```

The harness builds native C++, starts only its private virtual worker, and verifies
compiled ST rising-edge idempotency, completion/progress, Stop, actual scan watchdog
and bounded mailbox behavior. It terminates only its own child worker and removes
only its own temporary test directory. It never invokes deployment or activation.

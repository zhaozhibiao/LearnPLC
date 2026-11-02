# Workbench basic axis worker

Python 3.10+, standard library only, little-endian Unix. This authored implementation is MIT licensed. It adapts the private workbench axis image to the existing EcMotion v4 length-prefixed JSON protocol; it does not start or discover a controller.

Run only with an explicit backend and existing socket:

```sh
python3 axis_worker.py --manifest /private/axis-manifest.json \
  --socket /private/axis.sock --controller-socket /private/ecmotion.sock \
  --backend EcMotionSimulator
```

`EcMotion` is an explicit physical-backend selection. Startup only sends HelloRequest and ReadSnapshotRequest. Lease acquisition happens after a new successful PLC scan actually submits a valid command. Neither read-only status polls nor repeated binary frames renew the lease. A scan watchdog is max(250 ms, three PLC intervals); the permitted PLC interval is positive and at most 1000 ms. Missing/stale native sample cycles, stale UTC timestamps, transport errors, ownership failures, PLC stop or scan stalls latch a failure, attempt Stop on owned selected axes, and release the lease. The worker does not reconnect, replay commands, retry ownership, or report aborted motion as Done. On an unreachable backend, Stop/release are best effort and the existing server lease timeout is the remaining protection.

The private stream receives exactly 2576 bytes: `<QQ` scan/success flags and 64 `<QQddQ` slots (request/kind/position/velocity/flags). Each complete command image receives exactly one immediate 5632-byte response: 64 `<9Q2d` slots (request/busy/done/error/aborted/enabled/homed/state/motion_id/position/velocity). Repeated scan counters also get responses but cannot renew the lease or advance the PLC watchdog. There are no unsolicited feedback frames. Independently, the idle worker polls read-only snapshots every 10 ms and publishes capability changes immediately; an unchanged capability heartbeat is atomically refreshed once a second without fsync, including before any native PLC connects. New backend commands trigger a fresh snapshot; ordinary scan images use the latest validated cached snapshot. This 10 ms observation cadence is independent of the configured PLC scan interval and controller's real axis cycle; it does not claim a synchronized 1 ms hardware observation. Both sides must handle partial reads and writes; no extra length prefix is used on this private binary stream. Controller JSON frames have the separate four-byte little-endian length prefix, capped at 1 MiB. Version, request identity, response kind and sequence must match.

Normal PLC EOF/reset ends that PLC session while retaining the listening socket. The old session stops/releases owned axes and clears partial commands and pending feedback. A fresh session probes the same physical connection without acquiring control; only a new PLC connection and a valid new command can acquire a new lease. No old request is replayed. A lost physical transport is never reconnected automatically, and a failed live PLC stream is not silently recovered. Malformed framing remains a terminal protocol error. This lets loader probes, program unload and the next explicit PLC load share the persistent private service safely.

Axis manifest keys: integer `SchemaVersion: 1`, `InterfaceVersion: "workbench-axis-v1"`, integer `PlcIntervalMilliseconds` (1..1000), and `Axes`. Each axis requires `Index`, `Name`, `ControllerAxis`, `Unit`, `MinPosition`, `MaxPosition`; optional positive finite `MaximumVelocity`, `MaximumAcceleration`, `MaximumDeceleration`, `MaximumJerk` accept null as unset. MaximumVelocity bounds both positive positioning speed and the absolute value of signed jog speed. Axes have dense indices 1..64 and are sorted by .NET ordinal logical name. Logical names are ASCII identifiers; names, controller mappings and units are at most 256 characters. Duplicate indices or case-insensitive names/controller mappings and nonfinite/reversed bounds are rejected. The SHA256 is over the exact manifest file bytes.

The owned regular capability file defaults to `<private socket>.capabilities.json` and can be selected with `--capabilities`. It is atomically replaced at mode 0600 and includes manifest digest, interface version, backend, PID and controller readiness. `PhysicalBasicAxis` requires EcMotion mode and actual native scan batch ABI3/action-receipt Hello capabilities. Simulator mode never sets physical readiness. `Continuous` and `MotionCycleApplied` are always false.

Command kinds are 1 Enable, 2 Disable, 3 Reset, 4 Home, 5 MoveAbsolute, 6 Jog, 7 Stop. Stop and Disable can preempt. A held Stop image refuses ordinary new commands. Request IDs are submitted once; a lower per-axis request is refused as exhausted/replayed. Actual state, homed flag, position and velocity come from snapshots. MoveAbsolute requires a positive newly returned device MotionId matching terminal motionDone/not-busy/Standstill feedback. Jog remains Busy until Stop or Disable; velocity is actual feedback for the ST InVelocity calculation.

Accepted Reset waits through intermediate ErrorStop/fault snapshots for confirmed fault-free Disabled or Standstill with zero actual velocity. Persistent failure to reach that terminal state reports error 2005 after five seconds. Scan watchdog and stale-feedback protection remain active during that wait.

Physical Home uses native `PlcScanBatchRequest` action kind **3** (distinct from workbench kind 4), a separate session/batch index/action watermark, and validates the matching accepted receipt's positive `appliedCycle`. Homed Standstill feedback must come from an actual sample cycle at least that receipt cycle. C# simulator Home is synchronous and uses the direct AxisCommandRequest fallback. Its existing SnapshotResponse emits sampleCycle 0, so simulator freshness uses UTC timestamp; this limitation must never qualify a physical backend.

Errors: 2001 mapping, 2002 scan watchdog, 2003 control ownership, 2004 transport/protocol/stale feedback, 2005 backend reject/fault, 2006 busy/held Stop, 2007 numeric/limit, 2008 unsupported completion proof, 2009 request replay/exhaustion. An unprovable accepted command causes conservative teardown instead of invented completion.

Verification:

```sh
python3 -m unittest discover -s linux/axis -v
python3 linux/axis/integration_simulator.py --simulator-dll /isolated-copy/src/EcMotion.Simulator/bin/Release/net10.0/EcMotion.Simulator.dll
```

The integration helper creates its own temporary private sockets, starts only the explicitly supplied simulator DLL, keeps its console input open, exercises all basic operations plus scan watchdog, then closes both processes. Build only a **copy** of original EcMotion sources in an isolated test workspace; do not write build outputs into the controller checkout. No test in this directory connects to a real device. Native AppliedCycle semantics are verified with fake protocol responses; physical drive settings and actual drive behavior remain unverified. This is a basic workbench interface, not a claim of complete PLCopen certification or continuous coordinated machining.

## Axis faults versus engineering availability

Terminal worker failures publish state 0 with enabled/homed/Done false for every
slot; unmapped slots likewise never expose cached samples. MotionReady remains
false after failure even if a later poll sees healthy data. The 88-byte feedback
slot and 5632-byte image remain unchanged. Fresh command errors and actual axis
ErrorStop retain their state/position/velocity so MC_ReadStatus and actual-value
readers can distinguish valid fault feedback from unavailable data. See
`test_feedback_validity.py` and the generated public interface tests.

`ControllerReady` and basic-axis capability indicate a valid live interface and mapping, rather than fault-free axes. `MotionReady` is a separate snapshot of motion health. A healthy bus with an axis in ErrorStop retains live feedback and engineering availability. Enable/Home/MoveAbsolute/Jog for that faulted axis are rejected before lease acquisition; Stop/Disable/Reset remain explicit recovery commands. An explicit command rejection does not reconnect or replay the request and no longer tears down the management interface. Unknown outcomes, transport faults and ownership loss keep the existing conservative teardown behavior. See `docs/axis-fault-management.md` for deployment and verification details.


## 通道故障与耗时诊断（2026-11-02）

v1/v2原生客户端在整帧超时或无效回包后永久关闭该连接，不重试未知运动。有效业务拒绝保留查询连接。能力文件增加FailureCode/FailureReason/RecoveryRequired/RecoveryAction/LinkMetrics；耗时统计仍按1秒心跳发布。只读探针为axis_link_probe.py。显式恢复入口、部署记录及限制见 [轴控恢复与链路验证](../../docs/axis-link-recovery.md)。

# Motion bridge contract examples

These snippets describe the planned ST API. They are **not an installed ST library**,
and cannot be compiled or downloaded until a versioned ST runtime binding and a real
Linux adapter are implemented and validated. The C# contract tests do not move hardware.
No third-party axis-control source was copied into this module.

Planned FBs: `MC_MoveAbsolute`, `MC_Home`, `MC_Stop`, `EC_ExecutePath`. Each takes
`Execute`, a stable `AxisRef`, and exposes `Busy`, `Done`, `Error`, `ErrorID`,
`SessionId` and `RequestId`. Move adds `Position`/`Velocity`. ExecutePath adds the
immutable `PathId`/`PathVersion`. Stop is an ordinary controlled stop, not emergency stop.
These are PLCopen-style names, without a PLCopen certification or full conformance claim.

The runtime binding must call `MotionFunctionBlock.Scan` once per PLC scan. The
first Execute rising edge captures an immutable `MotionRequest`; a held high input
never submits again, even after consumption or reset. The receipt remains latched
until `ConsumeReceipt()` or `Reset()`; reset is rejected while Busy and does not cancel
device motion. A new command needs a later low/high edge. Request IDs increase across
FB instances within a process; a new runtime must choose a new random session UUID,
so restarting does not replay an earlier session's identity.

`IMotionBridge.TrySubmit` performs immediate local work only. `BufferedMotionBridge`
uses a bounded queue and starts unavailable. A separate worker, outside the PLC scan,
must own the physical connection, verify protocol capabilities, renew the lease,
publish coherent immutable snapshots and the installed path catalog, and call
`TryReadForDispatch`. A returned item with a nonzero error must not reach hardware;
report a matching failed `MotionFeedback`. Queue acceptance or dequeue is not device
acceptance, application or completion. Only actual device feedback can complete an FB.

Feedback must carry the original session/request identity and a fresh timestamp in
the bridge's agreed clock domain. The real adapter must translate device timestamps;
do not compare unsynchronized device wall clocks. Matching stale/future feedback and
deadline expiry produce FeedbackExpired; unrelated receipts are ignored. No retries
occur on timeout. Expiry means outcome unknown: motion may still be active and requires
device reconciliation. Terminal receipts cannot be overwritten by late success.

Lease ownership/expiry, snapshot age, mapping version, and the exact path version are
checked on submission and again immediately before dispatch. The Linux core must
atomically enforce the same lease token/mapping and idempotent request identity to
close changes between the local check and device acceptance. It must also enforce
limits, units, axis membership and its actual stopped/disconnection strategy. This
module does not implement EtherCAT/PDO/CiA402 or insert scan-rate trajectory points.

Suggested ST call sites (interface sketch only):

```iecst
(* AxisRef comes from a versioned hardware binding; not an axis display name. *)
Move(Execute := StartMove, AxisRef := AxisA,
     Position := TargetPosition, Velocity := TravelVelocity);
Home(Execute := StartHome, AxisRef := AxisA);
Stop(Execute := RequestControlledStop, AxisRef := AxisA);
Path(Execute := StartPath, AxisRef := AxisA,
     PathId := SelectedPathId, PathVersion := SelectedPathVersion);

IF Move.Done OR Move.Error THEN
    (* Explicit runtime receipt-consumption binding, then lower Execute
       before requesting the next move. Never substitute 'latest' path. *)
END_IF;
```

Physical Linux protocol serialization, asynchronous feedback ingress, ST ABI types,
unit conversion and receipt-consumption binding remain integration work. No live
transport is registered by this project, and missing connection never reports success.

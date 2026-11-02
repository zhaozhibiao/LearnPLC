# Standard engineering-axis interfaces

Approved choice: improve public MC_* blocks first; keep only business sequencing in project ST. No composite enable/home/round-trip block in this task. Work in the user's existing D:/OPenplc folder and preserve existing changes.

## Public contract

- Execute-based positioning, homing and reset blocks submit once on a rising edge and latch accepted inputs. Dropping Execute does not cancel active work. Busy remains true until correlated terminal feedback; terminal Done/Error/CommandAborted is retained while Execute is held and observed for at least one invocation when Execute was dropped early. A new accepted request clears prior outputs. A subsequent unrelated command must not retroactively turn an already completed instance into CommandAborted.
- The existing backend rejects competing ordinary commands. This task does not add buffering or blending to basic axis blocks and does not expose unsupported inputs as effective settings.
- MC_Power remains level-controlled with live Status, Busy, Error and ErrorID. It does not have a fictitious Done. It never automatically enables after a failed request or reconnect.
- MC_MoveVelocity exposes InVelocity rather than a fictitious completed-position Done. Existing compatibility outputs may remain. InVelocity requires valid correlated feedback and the accepted axis/velocity.
- MC_Stop locks its axis while Execute remains true; actual completed stop is Done. Busy stays true while the completed Stop is held; falling Execute releases the lock. Stop and Disable are the only existing preemptors. No other command can steal the stop lock.
- Read blocks expose Enable, Valid, Busy, Error, ErrorID and their live payload. Fresh actual ErrorStop state remains readable. Command rejection must not make fresh position/status unavailable. Worker failure/stale transport must invalidate data and never manufacture Done.
- Add MC_ReadStatus as the canonical standard-style status reader; keep MC_ReadAxisStatus working as a compatibility name. Expose standard state flags, with Homed documented as an extension where retained. No claim of full PLCopen certification.
- The existing fixed scan ABI stays unchanged; completed command feedback remains correlated. Actual limits, following-error, homing and completion policy continue to come from hardware configuration and native controller.

## Library and user experience

Catalog, drag/drop and MCP use the same interface descriptions, including named inputs and actual instance outputs. Generated code must not add separate copied output variables or an external ready/error state machine. Existing input placeholders may remain to preserve valid, inactive-by-default insertion.

A new concise round-trip example uses instance outputs for transitions and reporting. It retains explicit enable/home/start/stop/reset operator inputs, 1.5 forward / 2 s dwell / 1 reverse behavior and current 30 rpm defaults. It must not repeat position tolerances, velocity thresholds, native limit enforcement, stop confirmation watchdogs or invented 71xx diagnostic codes in Main. Business timing and step sequencing remain visible.

## Validation and rollout

Test compiler-generated blocks in memory for terminal retention/reset, early Execute release, new requests clearing old errors, stop locking, power state across subsequent commands, reader validity during command errors/faults and disconnection. Preserve actual identity and watchdog gates. Run core/catalog tests, worker tests, real compilation, example memory sequencing, desktop/MCP relevant tests. Do not download to the real device or move axes as part of this implementation. Old compiled programs keep their prior embedded library until explicitly rebuilt/downloaded.

Reference: PLCopen Motion Control Part 1 v2.0, public function block behavior rules and individual MC block contracts; implementation remains original MIT code. Only a supported subset is implemented, not complete parameter or buffer-mode conformance.

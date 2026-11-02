# Runtime motion configuration contract (Task 2, offline source candidate)

The durable overlay and `source-manifest.json` define the source candidate. No controller was contacted, no device parameters were written, and no binary was deployed in this task.

## Engineering interface

`driveDemandProtection=false` preserves the legacy host-target following-error reference. Selecting `driveDemandProtection=true` requires CSP mode 8, explicit `pdoEntries.positionDemand` and `pdoEntries.followingError` bindings, and positive finite `positionDemandRawPerUnit` and `followingErrorRawPerUnit` scales. Both scales mean raw integer values per configured `positionUnit`; neither is inferred from the encoder or PDO key spelling. For example, encoder counts/mm, demand counts/mm and 60F4 raw/mm may differ. `mechanics.direction` applies when converting signed values.

The selected entries must resolve to distinct 32-bit controller inputs, distinct from actual position. Missing entries and unknown scales are rejected. The existing layout does not contain EtherCAT object identity or firmware capability metadata: the engineering hardware package must validate the bindings against its verified device/ESI identity. Direction/width checks alone do not prove firmware support.

Position and demand travel must fit signed int32 after scaling. Velocity feedforward must fit the signed int32 output range. Invalid/non-finite scales, unreachable travel and following-error timeout cycle-counter overflow are rejected before motion. Soft limits, overspeed, raw feedback, mode, driver fault and timing checks remain active.

Master thresholds remain `motionSafety.maximumFollowingError` (engineering units) and `followingErrorTimeoutMilliseconds`. With `driveErrorOnly=true`, the auxiliary following monitor uses the declared 60F4 error. With it false, it uses the larger of absolute demand-minus-actual position and 60F4 error. `driveErrorOnly=true` requires `driveDemandProtection=true`.

`demandDeliveryTolerance` is nonnegative and finite in engineering units. `demandDeliveryEnforced` defaults to false: the historical fixed-delay comparison is diagnostic. Explicit true retains the diagnostic enforcement behavior for a device whose delay has been measured. The comparison remains the existing two-entry history against the entry image target, and does not claim to discover drive latency. Startup/unreferenced raw following-error supervision remains active until auxiliary supervision is eligible.

The status response retains `cycle_error` as host target minus actual and adds `demandPositionError`, `driveFollowingError`, `demandDeliveryError`, `demandFeedbackValid`, `demandDeliveryMismatch` and `demandDeliveryFault`. Errors are engineering-unit values; validity/mismatch/fault are separate. `timingReady` and `timingFault` retain their meaning.

Drive internal 6065/6066 windows and firmware support belong to the verified hardware/startup-SDO configuration. The master timeout does not imply a 6066 implementation or an internal 20 ms drive delay.

## Supported trajectory semantics

`maximumJerk=0` keeps the legacy planner. A positive value selects rest-to-rest quintic MoveAbsolute. A new smooth move requires the generator to be in Hold with exactly zero commanded velocity. An active move, stopping command, jog or external path cannot silently fall back to the legacy MoveAbsolute planner. Rejected commands leave the active trajectory unchanged. A subsequent move can start after the stop reaches Hold.

Stop uses the existing configured maximum deceleration and is not jerk limited. Jog and external/group interpolation retain their existing semantics and are not made jerk limited by `maximumJerk`. Disable/fault feedback synchronization discards the old curve. Non-finite synchronization invalidates motion authorization until a finite synchronization succeeds. No completed high-speed velocity, acceleration, deceleration, jerk or travel defaults were changed.

## Verification and remaining gate

The extended `smooth_profile_selftest.cpp` first failed on unsupported smooth reentry, then passed after the fix. A separate added timeout-counter-overflow test also failed before its fix and passed afterward. The existing `motion_setpoint_selftest.cpp` passes. Both were compiled from the modified source on Windows using MinGW GCC 6.4 with `artifacts/task2-gcc6-shim.hpp`, which only supplies the standard `clamp` missing from that old library; ignored `nodiscard` warnings were disabled. This checks the portable motion/safety logic, not a release C++20/IgH build.

`drive_demand_protection_selftest.cpp` now also covers arbitrary project keys, engineering units with direction -1, 2 ms cycles, independent demand/error scales, exact 20 ms error dwell, feedback freeze, invalid/missing mappings/scales, int32 travel limits, Stop/Disable/reversal, and separate diagnostic/enforced delivery status. Existing mode, timing, limit, overspeed, driver fault and reset tests remain. Snapshot exchange coverage was extended in `motion_correction_selftest.cpp` to check all six new diagnostic fields. These adapter tests have not been compiled or executed locally because a suitable C++20/IgH development environment is unavailable. Local CMake configuration reached and failed the missing-IgH check. Do not treat Task 2 as release-validated until the full build and CTest below pass.

The thirteen modified snapshot source paths are recorded in `artifacts/task2-motion-files.json`; the durable overlay includes all 28 manifest entries. Build on an authorized Linux build host from the exact known source baseline plus this verified overlay, then run:

```
cmake -S src/EcMotion.Igh -B build-runtime-motion -DECMOTION_IGH_ROOT=/path/to/matching/igh -DECMOTION_IS620N_PROFILE=ON
cmake --build build-runtime-motion -j4
ctest --test-dir build-runtime-motion --output-on-failure
```

The root task independently owns capability-report changes in `main.cpp`, `native_protocol_server.cpp` and `include/motion_runtime_version.hpp`. It confirmed these account for the observed snapshot/manifest difference; final packaging must include its capability changes and refresh the affected durable entries. This subtask did not edit main.cpp or the version header.

# Axis v2 engineering and packaging

The verified v2 interface exposes `Acceleration`, `Deceleration` and `Jerk` on `MC_MoveAbsolute` and `MC_MoveVelocity`, and `Deceleration`/`Jerk` on ordinary `MC_Stop`. Protective Stop and Disable retain their safety priority. Each defaults to `0.0`, meaning inherit the explicitly configured axis default. Acceleration/deceleration defaults must be finite and positive; jerk must be explicitly finite and nonnegative. Configured jerk `0` selects the legacy profile. A command jerk `0` inherits the configured value, including a positive limit. Negative, nonfinite and above-limit inputs are rejected; requests are not silently clamped.

The instruction catalog inserts dynamic inputs for new absolute moves, velocity moves and ordinary stops. Existing programs omitting those inputs continue to build with `workbench-axis-v1`. The lexer and symbol index select v2 only when a declared motion FB instance is called with a top-level named dynamic input. Comments, strings, nested unrelated function arguments and unrelated function calls do not select v2. Once any dynamic call selects a v2 program, every actual MoveVelocity or Stop call, including omitted dynamic inputs, additionally requires `workbench_axis_velocity_profile_v2`, following verification of the bounded velocity transition planner. Stop has no Acceleration input.

The v2 ST library is cloned from the existing library, preserving stop ownership, terminal-result visibility, power recovery, feedback correlation and status behavior. The request arbiter samples the inputs on Execute's rising edge, validates them before incrementing the request identity and writes all profile fields with that request. Changes while Busy do not mutate its command image. The worker and native engine also validate engineering maxima and effective inherited defaults.

V2 packages use `tools/axis-bridge-v2` and bundle `axes/axis_worker_v2.py`. Their axis manifest declares `workbench-axis-v2`, `CommandBytes:4112`, `FeedbackBytes:6656` and `RequiredRuntimeCapabilities:["workbench_axis_move_profile_v2"]`; all Jog/Stop calls in a v2 program additionally require `workbench_axis_velocity_profile_v2` in both manifests. The build manifest declares the matching axis interface, sizes, `AxisExportMarker:"workbench_axis_v2"` and native capability. Input identity includes the selected library/bridge/worker hashes and interface version. V1's axis manifest and worker path retain their existing form.

The private v2 transport and native capability must be verified before application replacement. Do not infer an ABI from packet length. Engineering checks do not constitute physical commissioning.

Offline validation:

```
dotnet run --project tests/StWorkbench.Core.Tests --no-restore
dotnet run --project tests/StWorkbench.Toolchain.Tests --no-restore -- --axis-v2-only
```

The second command invokes the pinned STruC++ 0.7.0 compiler for v1 and v2 programs, including an omitted-input absolute move in a v2 program, and checks package identities, version metadata, capabilities, worker and bridge marker.

The smoke records `artifacts/axis-v2-package-verify/last-v2-package.txt` and `last-phase2-package.txt`. Use the latter for the complete runtime fixture. On Linux, unpack that package into `$candidate` and compile the generated ST runtime fixture:

```sh
g++ -std=c++17 -O2 -I"$candidate" -I"$candidate/strucpp_runtime/include" "$candidate/generated.cpp" tests/StWorkbench.Toolchain.Tests/axis_v2_st_semantics.cpp -o /tmp/axis_v2_st_semantics
/tmp/axis_v2_st_semantics
```

This fixture exercises the actual compiled FB state machines with synthetic scan feedback, without starting the axis worker or connecting to motion hardware.

V2 feedback has 64 slots of 104 bytes (`9Q2d` accepted-owner feedback plus rejected-request/rejected-error `2Q`), totaling 6656 bytes. Definitively rejected requests have a separate receipt and never replace the accepted owner's progress. The ST arbiter observes supersession only after a newer accepted feedback identity; a locally incremented counter alone is not acceptance. Rejected request errors terminate only the matching FB. The retained accepted move can still report its real motion-ID-correlated terminal result.

The outgoing request image and request sequence are separate internal ST values. If an older accepted held Stop must be released after a newer rejected request, ST re-sends that accepted Stop identity with the hold released. The sequence retains its high-water mark, so the next new command never reuses a rejected identity. Releasing the rejected FB cannot release the previous accepted Stop.

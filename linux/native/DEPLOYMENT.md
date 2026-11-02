# Native deployment coordinator

`st-deployment-coordinator` reads one bounded JSON request from stdin and emits
one JSON response. It reads only the private fixed configuration
`/home/a/st-workbench-deployment/config.json`. It does not run Python and never
uses the native Runtime upload endpoint.

The CLI preserves staged compile/activate transactions, explicit start/stop,
standalone hardware transfer, configuration plans and scoped source/debug-map
uploads. Runtime state and commands travel through the authenticated local HTTPS
manager, which owns PLC IPC. Credentials and authorization headers are private
temporary files, not command arguments.

Native candidates carry `NativeWorkerInterfaceVersion=workbench-linux-native-v1`,
`NativeSources`, `NativeSourceBundleSha256`, and `NativeWorkerTargets`. Sources
are inventoried under `native/`. Bundle identity hashes ordinal-sorted UTF-8
`path + NUL + lowercase-file-SHA256 + LF` records. The coordinator independently
records compiler output and ELF hashes in `native-build-receipt.json`; source
hashes are never compared directly to executable hashes.

Worker installation creates fixed user service units and private manifests plus
`worker-receipt.json`. Admission checks the candidate source bundle, compilation
receipt, installed executable hash, running process executable inode, private
socket owner/mode, fresh capability record, native hardware identity and worker
image version. Physical continuous execution remains unavailable; the supported
path worker is explicitly virtual.

Lifecycle entry points:

- `--start-active-workers`: starts only verified workers belonging to the active
  application's direct native candidate or a committed legacy binding. It does
  not request PLC START, enable axes, or issue motion.
- `--stop-workers`: stops only the fixed ordinary-user worker services.
- `--recover-axis EXPECTED_BUILD`: recovers the axis bridge only after a stopped
  PLC, unchanged active identity and core PID, applied hardware proof, and actual
  stationary feedback. It does not restart the PLC program or motion.
- `--bind-legacy-active NATIVE_BUILD`: migrates an already active application's
  worker implementation without modifying its immutable original manifest or
  compiled program bytes. PLC STOPPED and actual stationary/disabled feedback
  are mandatory. The new compiled native worker must have the same image ABI
  and exactly the same axis/communication/path manifest bytes.

Legacy migration writes `legacy-native-migration.json` with `schemaVersion=1`,
`legacy_active`, `native_build_hash`, `source_bundle_sha256`, `native_targets`
(compiled paths and ELF hashes), and `native_core_pid`. States advance through
`prepared`, `installed`, and `committed`; `verified=true` is written only after
the final core, manifest, executable, PLC-state and stationary checks pass.
Incomplete bindings cannot authorize old-program START. A normal status query
includes `native_migration` for inspection after interruption. A retry is an
explicit operator action and repeats preflight; there is no mutation replay.

Build `src/deployment_coordinator.cpp` plus `src/axis_recovery.cpp`, link
`st-native-common` and libarchive. Tests are `deployment_archive_test.cpp` and
`deployment_coordinator_test.cpp`. The second test optionally accepts
`ZIP_PATH SHA256 BUILD_HASH` to validate a real newly generated program package.

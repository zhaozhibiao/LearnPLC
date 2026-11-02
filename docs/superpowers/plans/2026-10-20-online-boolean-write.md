# Online BOOL single-write implementation plan

**Goal:** Set operator BOOL inputs such as ParametersConfirmed and RunOnce from the bound online engineering station, with explicit confirmation and no persistent forcing.

**Architecture:** New bounded debug extension FC46 reports support and queues one BOOL write with expected program MD5. The existing dispatcher write journal applies it outside IEC task execution. Authenticated .NET transport permits this narrow operation only; UI offers matched top-level user BOOL symbols, checks freshness and identity before/after confirmation, and sends once. A queued acknowledgement is never reported as confirmed variable state; monitoring provides readback. Legacy FC42 forcing is not used.

**Protocol:** query `[46,00]` → `[46,7E,01]`; write38bytes `[46,01,MD5ascii32,array8,elementBE16,bool8]` → `[46,7E]` queued. Failures81bounds,82queuefull,83notloaded,84identity. Unsupported runtimes never receive a force fallback.

- [x] Native handler TDD and durable MIT patch/provenance; address/size/value/MD5 checks and serialized soft-write path.
- [x] .NET debug transport TDD: exact packets, unsupported capability, changed identity, invalid sizes, cancellation, unknown outcome/no replay.
- [x] UI TDD: user BOOL filter, live matched identity/freshness, concrete confirmation, stale operation guards, write-once and pending/readback status; preserve read-only defaults.
- [x] Build native tests and patched Runtime without device access; stage user-owned executable with checksum/backup and syntax checks.
- [x] Inspect actual PLC stop state before any Runtime restart; maintain stopped state and current program identity. No TRUE write to test start/enable inputs, no motion test under this task.
- [x] Full relevant .NET/native verification, independent review, live capability proof and operator instructions.

Device192.168.1.31 is the already authorized private test Runtime. Runtime upgrade does not grant new root authority or modify EtherCAT configuration. Preserve unrelated dirty work and existing authoring projects.

## Verified outcome

2026-10-20: full solution build0warnings/errors; DebugSocketClient tests passed, OnlineWrite UI tests included in defaultApp regression and passed; existing deployment71tests passed. Native handler252assertions passed locally and Linux; actual fullLinuxRuntime built with -Werror, SHA66dbd3aa386e923cf403399beddaadeafd76ff5059232b70e45b4e3baff357b5. Upgrade stopped PLC normally after read-only proof RunOnce/ParametersConfirmed/Active allFALSE, kept originalactive7A4F... and restoredoriginalRUNNING; EtherCATaxisDisabled and commandsequence0. Authenticated .NET/WSS live test wrote only FALSE to ParametersConfirmed(alreadyFALSE), readallthreecontrolsFALSE; no TRUE, force, or motion. Independent review found no serious issues. See linux/debug-write/live-verification.json and docs/online-boolean-write.md. Runtime source backups and oldbinary retained under private debug-write deployment directory.

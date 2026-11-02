# OpenPLC v4.3.0 one-shot BOOL write

This source patch adds FC `0x46` to the native OpenPLC executable. It uses the
existing `DBGW_OP_WRITE` dispatcher journal, never force followed by release.
Upstream SPDX MIT and copyright notices are retained in the existing source files.
The staged upstream reference is unchanged.

## Wire protocol (debug PDU, transported by existing DEBUG command)

| Request | Response |
| --- | --- |
| `46 00` (exactly 2 bytes) | `46 7E 01` capability version 1, even without a loaded PLC |
| `46 02` (exactly 2 bytes) | `46 7E 01` task scheduling ABI version 1; readonly and available without a loaded PLC |
| `46 01`, expected program MD5 (32 ASCII bytes), array (1 byte), element (2 bytes big endian), BOOL (`00` or `01`) — exactly 38 bytes | `46 7E` queued |

Errors are `46 81` (length/op/value/address/size), `46 82` (queue full), `46 83`
(program not ready, not RUNNING, missing required exports, or malformed MD5),
and `46 84` (program identity mismatch). Start the PLC before requesting a write.
The request must carry the MD5 from the same build as its debug address map.
Success acknowledges enqueue only, not application or a completed machine action.
An existing force wins over this ordinary write. The exported size must be one
byte; the protocol has no runtime type-tag export, so a BOOL-aware client/debug
map must select the correct leaf. FC41–45 retain their prior implementations.

## Lifetime and locking

`debug_write_program_begin_load()` locks a dedicated static lifecycle mutex,
invalidates readiness, and resets the journal. `symbols_init()` holds it while
resolving all exports, then calls `debug_write_program_end_load(rc == 0)`.
FC46 holds the same mutex across readiness/RUNNING checks, MD5 comparison,
address/size validation, and enqueue. `unload_plc_program_locked()` calls
`debug_write_program_invalidate()` before dispatcher join and any export clear
or dlclose. Invalidation waits for in-flight FC46 enqueue, closes the gate, and
resets the journal. Later FC46 calls cannot dereference exports until successful
initialization of the next program. Initialization failure leaves the gate closed.

The gate is never held across dispatcher join. New nesting is lifecycle gate →
debug journal only; dispatcher image → journal order is unchanged. Existing
other protocol/read synchronization is unchanged; this patch specifically
protects the new FC46 path. The RUNNING check is a snapshot: STOP can follow a
queued acknowledgment and discard that write, which is intentionally permitted.

## Source integration and checks

`provenance.json` records SHA-256 before/after for every source file. The installer
checks all target revisions before writing anything, validates packaged hashes,
accepts already installed files, and rejects the staged upstream reference.
It performs no build, service, network, or machine operations.

```powershell
python linux/debug-write/tests/run_tests.py linux/debug-write/src/debug_handler.c --cc C:/mingw64/bin/gcc.exe
python linux/debug-write/package.py --install <runtime-source-build-copy-root>
```

Linux offline handler check:

```sh
python3 linux/debug-write/tests/run_tests.py linux/debug-write/src/debug_handler.c --cc cc
```

Alternatively apply `debug-write-v1.patch` from a pristine v4.3.0 source root with
`patch -p1`. Rebuild the native runtime executable using that installation's
normal build procedure. Rebuilding only the PLC `.so` cannot add this protocol.
Changing a live installation or restoring a running PLC is a separate operation.

Tests compile the real handler source against boundary stubs with warnings as
errors. They check capability, both BOOL values, exact length, invalid operation,
indices (including big-endian element 258), size, MD5, unavailable exports,
RUNNING-only access, queue overflow, initialization failure, invalidation and
enqueue serialization, and FC41–45 regression behavior. They do not execute a
PLC scan or contact hardware. Full Linux runtime build is still required.

## Explicit PLC task scheduling

New compiler packages export the optional C function
`int workbench_task_scheduling_v1(unsigned resource, unsigned task, int *policy, int *priority, int *cpu)`.
Indices follow the native configuration declaration order. Every declared task
must return `1`; a missing record or malformed value rejects startup. Policy `0`
selects `SCHED_OTHER` with native priority zero; policy `1` selects `SCHED_FIFO`
with IEC priority `0..31` mapped to native `49..18`, below motion FIFO 80.
CPU `-1` requests no pinning; nonnegative values request one Linux CPU.
Unavailable or forbidden CPUs cause startup failure through the affinity API.
The new program's dispatcher remains ordinary. The watchdog's safety policy is
unchanged.

The bootstrap applies scheduling and affinity while every worker is blocked on
its first release. Any explicit scheduling/affinity error joins all created
threads, publishes ERROR and never releases a scan or publishes RUNNING. Missing
export means a logged legacy mode retaining upstream FIFO priorities and fallback;
it does not imply that a task was explicitly marked ordinary.

Before deploying packages that require ABI version 1, query `DEBUG:4602` on the
running native runtime and require the exact decoded response `46 7e 01`.
Rebuilding only a program shared library does not add runtime support. The
capability response belongs to the full source patch; install and rebuild all
packaged sources together.

```powershell
python linux/debug-write/tests/run_scheduling_tests.py --cxx C:/mingw64/bin/g++.exe
```

Scheduling tests compile the shared policy helper and the actual bootstrap
creation/configuration transaction extracted unchanged from the runtime source,
using OS boundary fakes. They cover ordinary/RT mapping, affinity, partial create,
permission errors, failure before RUNNING, and explicit legacy fallback. They do
not replace a complete Linux runtime build or live scheduler verification.

Observed test-first evidence: original handler failed at the first capability
assertion. A short-MD5 case subsequently failed before bounded identity checking
was fixed. Final local harness: **252 assertions passed** using MinGW GCC 6.4.0.

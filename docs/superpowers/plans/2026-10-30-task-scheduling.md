# Task scheduling execution
Approved spec: ../specs/2026-10-30-task-scheduling-design.md

1. Core/App/MCP: explicit Ordinary/Realtime, priority and CPU metadata. Snapshot and UI roundtrip; multiple enabled unique program bindings. Tests for defaults, identity and invalid combinations.
2. Compiler adapters: generate multiple IEC tasks and versioned exported scheduling metadata; ordinary Main explicit. Bridge adapters gate unsupported multi ownership. RED/GREEN adapter tests.
3. Runtime: resolve exported metadata from loaded program; select SCHED_OTHER/FIFO, apply affinity, fail task startup when declared realtime cannot be honored. Preserve legacy package interpretation. Startup must not execute usercode before all workers admitted. Native fault tests.
4. Toolchain: propagate metadata/hashes; allow base multi task; reject extensions with shared single scan assumptions. Preserve package identity and source binding.
5. Review full diff, run Core/App/MCP/Toolchain regressions and Linux native tests. No automatic hardware movement or CPU boot configuration. Remote deployment only after package compatibility verified, safe mode first.

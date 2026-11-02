# Task scheduling policy approved in conversation

Motion cycle: highest application real-time class; independent physical core, keep its SMT sibling unused. PLC explicitly configured scan tasks: real-time below motion, priority ordered. Default Main: periodic SCHED_OTHER. Management, logging, monitoring: SCHED_OTHER. Keep kernel dependencies/watchdog priorities deliberate; do not assign all threads FIFO99.

Current audited gaps
- PlcTask and TaskSnapshot carry interval/enabled but no scheduling policy or priority.
- ProjectValidator requires exactly one enabled task; supporting default Main plus explicit realtime tasks needs multi-task build/validation support, not just extra UI fields.
- PackageBuilder currently chooses one active task and its interval for communication/axis bridge generation; bridge cycle ownership must be explicit before multi-task execution.
- Linux plc_task_body applies FIFO to all workers and falls back to ordinary scheduling on failure. Explicit realtime task startup must fail clearly when FIFO/affinity cannot be applied; Main must explicitly request ordinary policy.
- CPU affinity in plc_state_manager.cpp is currently initialized to0; no end-to-end metadata binding is implemented there.
- Motion UI default priority40 differs from diagnostic native core FIFO80. Unified priority rules need compare actual mapped Linux values, not IEC numeric values directly.

Implementation sequence
1. Version task metadata: explicit scheduling policy, IEC priority, optional CPU affinity; preserve old package interpretation until migrated explicitly. Default new project Main ordinary-periodic. Task and program identity are separate: a program named Main does not override an explicitly assigned task policy.
2. Propagate through snapshots, compiler adapter, package identity, upload/export, MCP and task properties. Validate duplicate assignment, cyclic interval, motion-core exclusion and priority hierarchy. Preserve single cycle owner for shared IO and motion bridge; reject unsupported combinations.
3. Native worker selection and fail-closed admission; management threads ordinary. Test priority conversion, non-RT Main, failed realtime admission, restart/stop, multiple periods and shared-image arbitration.
4. Physical CPU isolation and persistent services with backup/rollback. Apply disabled-first, then validate actual per-thread scheduler/affinity and excluded sibling/IRQ/ordinary workloads. Boot changes require controlled reboot, never automatically enable motion.
5. Full live lifecycle verification before speed testing or claiming scheduling completed.

No runtime code, CPU isolation, or device state changed by this audit. This document records scope and blockers, not completion.

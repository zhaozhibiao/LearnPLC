# Task scheduling

Implemented policy
- New Main/default task: Ordinary periodic, Linux SCHED_OTHER. Period and realtime policy are independent.
- Explicit Realtime PLC tasks: IEC priority0..31 maps to Linux FIFO49..18. Optional CPU affinity. Motion default FIFO80; allowed60..89, above PLC workers. Management remains ordinary. Task names never override an explicit scheduling choice.
- Edit task properties from the resource tree: enable, period, program, scheduling, priority and CPU. Snapshot, package identity, engineering archive and MCP configuration preserve attributes.
- Base ST adapter supports multiple enabled tasks with distinct program instances. Axis/path/control/communication bridge adapters intentionally retain a single task owner until their shared scan lifecycle supports concurrent tasks. Unsupported combinations fail validation/build rather than silently selecting a task.

Runtime admission
- New libraries export workbench_task_scheduling_v1 indexed by resource and task. Missing/invalid rows fail admission; all created worker threads wait until scheduling and affinity are accepted before RUNNING. Failure reaps pending workers without executing scans.
- Packages advertise workbench-task-scheduling-v1. Coordinator requires the actual library export and a readonly running-runtime DEBUG:4602 response467e01 before activation. Unpatched runtime and unsupported REST capability discovery reject new packages. Upgrade coordinator and runtime together; bypassing coordinator with manual library copy is not the supported installation path.
- Libraries without scheduling export retain historical legacy runtime behavior. Loading an old library does not silently convert its scheduling; regenerate a new package to apply explicit policy.

Verification2026-10-30
Core, full headless App, full MCP, compiler4cases, native scheduling/admission regression,255 real debug handler assertions passed. Deployment5scheduling+18coordinator+11motion-cycle tests passed. Actual ST multi ordinary/realtime candidate and changed-policy identity passed; control/communication/axis/path composed package smoke passed.
Linux complete runtime built in independent directory /home/a/st-workbench-i210/task-scheduling-20261030/build/plc_main; SHA256529992316f08a4a77314fdd5c4f5c815122fd2311ab6daf7e7addd5c19624185.

Deployment status
Workspace implementation and independent Linux build completed. Running controller services were not replaced/restarted. CPU physical isolation, SMT sibling exclusion and actual deployed-task scheduler verification remain separate required deployment work. No motion command was issued by this scheduling change. Successful compilation is not proof of live realtime bounds or commercial redistribution clearance.

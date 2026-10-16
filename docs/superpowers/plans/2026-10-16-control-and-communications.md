# Control and Communications Implementation Plan

> **For agentic workers:** Use subagent-driven-development and verification-before-completion. Work in the user-authorized existing folder and branch. No physical serial/network writes during automated tests.

**Goal:** Supply 15 executable control blocks plus Linux Modbus-master, serial-point-to-point and TCP-client communication callable from ST, with engineering/MCP integration.

**Architecture:** Original MIT ST libraries plus bounded native scan bridge. Linux worker does non-real-time communication, fixed arrays exchange requests/feedback. Nullable engineering model preserves old snapshots when unused; compiler injects only used libraries and packages exact sources.

**Tech stack:** Existing .NET10/Avalonia/STruC++0.7.0; standard-library Python3.11 for Linux transport/termios/select, C++17 bounded bridge, original ST algorithms.

## 1. Control library
- [x] Write numeric-vector/invalid-value/bump-transfer tests before code. Create tools/control-library/control.st and LICENSE, plus actual compiler-generated native execution harness under tests/ or tools/control-library/tests. Blocks: WB_PID, WB_INCREMENTAL_PID, WB_CASCADE_PID, WB_ONOFF, WB_LOW_PASS, WB_MOVING_AVERAGE, WB_MEDIAN, WB_KALMAN_1D, WB_SLEW_RATE, WB_LEAD_LAG, WB_INTEGRATOR, WB_DERIVATIVE, WB_SCALE, WB_DEADBAND, WB_TIME_PROPORTION. LREAL, bounded loops, explicit seconds, original implementation. Validate actual generated code, not a second-language clone. Independent review and resolve findings.

## 2. Linux communication engine
- [x] Create linux/communication worker and tests first: TCP loopback byte-level independent fixtures; Linux PTY serial/RTU tests; CRC golden vectors; FC01/02/03/04/05/06/0F/10/16/17, exception responses, broadcast no-ack semantics, expired/partial/wrong replies, no uncertain-write retry, port exclusivity, missing RS485 support, closure/watchdog. Standard library only; production termios/RS485 ioctl isolated behind transport. Fixed request interface 16 channels, 512-byte data/response, one pending command per channel. Native IPC receives bounded JSON-lines or fixed images from bridge. Worker shutdown/fresh-scan watchdog drops queued writes and closes ports.

## 3. Engineering model and ST/native bridge
- [x] Add nullable CommunicationConfiguration to project/snapshot and validation/capture/persistence. Channel names ASCII, fixed ordinal mapping; endpoint, serial and framing rules validated. No models add server roles. Existing source/hardware identities unchanged when absent. Configuration included in frozen build identity when used.
- [x] Create tools/communication-bridge original ST library and C++ scan mailbox/worker/lifecycle. Command image has scan counter and slots with request, action, function, address, quantity, auxiliary mask/read parameters and bounded BYTE data. Feedback carries matching ID, busy/done/error/unknown, count and data. Execute rising edge, no resubmit, reject same-channel busy, connect/send/receive/close and Modbus transactions. STOP/PLC unload stops accepting new requests; no blocking work inside scan.
- [x] Extend compiler adapters with shared additional-library injection and scan hook composition (ordinary/axis/path candidates). Include active control library and communication bridge/worker in package inventory and build identity. Extend Linux candidate validation/bootstrap and communication-ready gate; no installed worker = explicit refusal. Preserve existing ABI/gates.

## 4. Desktop and MCP
- [x] Add communication configuration panel inside hardware/network pages; keep current layout. Stable channel constants and actual executable snippets. Control block resources use authoritative interface names; signatures/catalog/MCP schema updated. Model changes use current draft revision transactions. Do not add arbitrary remote-write MCP proxy. Tests cover save/reopen, UI edits/rollback, insertion/undo and MCP discovery.

## 5. Verification and delivery
- [x] Run actual compiler positive/negative and standalone generated-code behavior, loopback/PTY communication, native bridge bounded queue and Linux candidate validation. Combine control+communication+axis/path compilation, preserve legacy package hashes when unused. Use alternate output directories so desktop can remain open.
- [x] Independent spec and quality review, fix confirmed defects, rerun affected suites. Update Chinese docs, examples and MIT/third-party license records. Record platform skips and untested real hardware; do not claim physical verification. Commit only task changes and preserve pre-existing frontend deletions/.vs.

# Generic ESI-driven EtherCAT implementation plan

> **For agentic workers:** Use subagent-driven-development for isolated ESI, configuration compiler and native loader work. Preserve the dirty user checkout and existing IS620N safety fixes.

**Goal:** Import ESI descriptions, select concrete PDO/DC/startup parameters, generate a bounded native topology/machine/IO bundle, and load that bundle on Linux with one generic native executable. Standard CiA402 CSP axes share the existing ST interface; unknown device-specific behaviors remain explicit blockers.

**Architecture:** Additive optional project metadata retains legacy JSON/hash behavior. ESI is descriptive input; a compiler produces deterministic reviewed runtime data and blocking diagnostics. A bounded native JSON loader configures IgH before starting the realtime loop. Hardware application is a separate explicit stopped-state transaction, with identity/hash proof required before PLC activation.

**Tech stack:** .NET 10/Avalonia, C++20/IgH/nlohmann JSON, Python standard library. Newly authored code MIT; existing upstream licenses unchanged.

The user approved implementation of the preceding three-part design: full configuration generation/download, generic CiA402 backend, device adaptation profiles. Routine design choices below implement that authorization.

## Work and verification

- [x] Preserve ESI PDO groups, SM watchdogs, DC operation modes and SDO startup commands, bounded XML and unsupported feature diagnostics. TDD in Core tests.
- [x] Optional station/axis runtime settings, deterministic topology/machine/IO generation; reject missing unit confirmation, conflicting PDO choices, unsupported states and motion-producing startup SDOs.
- [x] Native `--ethercat-config` loader; validate before requesting master; preserve static fallback, exact identity/revision checks, homing and all existing safety behavior. Offline native loader and legacy CTest.
- [x] Add controls within existing hardware pane for cycle, PDO/DC/profile selection and axis commissioning parameters; legacy projects remain editable.
- [x] Include runtime bundle in package inventory and build identity. Preserve legacy bundles without new opt-in.
- [x] Private device hardware staging/application command: fixed root-owned generic executable, validated bounded data only, stopped-state and disabled-axis checks, atomic active configuration and explicit failure state (no unverified rollback). No implicit PLC Start or axis Enable.
- [x] Native Hello and worker capability report exact loaded hardware fingerprint; candidate activation verifies fingerprint when generic runtime bundle is present.
- [x] Build generic core and verify existing IS620N configuration via the dynamic path. Only apply to the confirmed isolated test service after checking actual stopped/disabled state; no new movement test required.
- [x] Full relevant .NET/Python/native verification, review, reproducible patch, updated operator documentation and evidence separating offline tests from live proof.

## Limits

Only complete supported ESI constructs are applied. Padding, modular slots, unsupported startup transitions or vendor behaviors must either be represented and validated or yield a clear blocker. CiA402 CSP support does not assert universal drive compatibility. Units/encoder/gear/homing and limits require explicit configured values. No XML value is treated as physical commissioning proof. A core restart is acknowledged separately from PLC program activation and must preserve stopped state until explicitly resumed.

## Verified outcome (2026-10-20)

All eight .NET test executables passed, including real STruC++ packages and UI regressions. Full solution build: 0 warnings/errors. Python deployment 68/68, privileged helper 13/13, axis 32 (4 optional integration skips); native CTest 11/11. Reproducible native patch proof matched all 16 output hashes without changing original files. Actual IS620N generic configuration applied and fingerprint verified; runtime/IO ready, Axis1 Disabled/fault-free/velocity zero, no commands accepted. Read-only PLC candidate compiled on device with axis_ready=true; original PLC program remains stopped and cannot start with mismatched hardware. See examples/generic-ethercat evidence JSON.

Automatic review rejected persistent passwordless sudo provisioning. That installer was replaced with per-operation authenticated sudo; no sudoers modifications were installed. A live preflight issue caused by OpenPLC single-owner native IPC was fixed with authenticated, certificate-validated local HTTPS status reads before successful application.

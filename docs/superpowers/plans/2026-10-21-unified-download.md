# Unified Download Implementation Plan

> Execute in the current user-requested workspace on codex/st-workbench. Preserve existing unrelated changes. Use test-driven-development and verification-before-completion; request a read-only code review before completion.

**Goal:** One download operation compares and applies the necessary axis/hardware configuration before activating a compiled program.

**Architecture:** Add a device read-only configuration plan and an identity-bound apply operation. Extend the existing tested ProgramDownloadWorkflow and its desktop host, retaining all candidate, active-program, connection and readiness checks. Reuse the restricted broker for physical hardware changes.

**Tech Stack:** Python deployment coordinator, C#/.NET workflow, Avalonia desktop; no new dependencies.

## Tasks

- [x] Add failing Python tests in linux/deployment/test_configuration_download.py for none/axis/hardware plans, candidate inventory, unchanged native core on axis update, stopped/disabled gates, plan identity mismatch, readiness and no retry on failure. Implement linux/deployment/configuration_download.py and coordinator routes.
- [x] Add failing tests in tests/StWorkbench.Deployment.Tests/ProgramDownloadWorkflowTests.cs for a single confirmation, configuration update, original state preservation, same-program repair and all uncertain result boundaries. Implement plan parsing, confirmation binding and configuration application in ProgramDownloadWorkflow.cs and RemoteDeploymentClient.cs.
- [x] Extend tests/StWorkbench.App.Tests/DownloadApplyUiTests.cs to exercise actual headless UI for a mismatched manifest and physical hardware update. Implement DownloadHost, candidate readiness metadata, frozen target transport and scrollable confirmation.
- [x] Run dotnet build StWorkbench.slnx -c Release -p:OutputPath=bin/unified-download-verify/ followed by deployment/app suites; run python3 -m unittest discover -s linux/deployment on Linux in an isolated directory. Run axis/MCP regressions as appropriate. Record output and correct material review findings.
- [x] Update docs describing one download and stop/restart consequences. Ship updated coordinator modules to the configured device without initiating configuration changes, PLC activation, enable or motion; verify read-only planning against the actual candidate if available. Rebuild shipped MCP and release desktop outputs where possible.

The device JSON contract is configuration_plan = {build_hash, kind, identity}, where kinds are none/axis/hardware and identities are 64 hexadecimal digits. configuration-apply takes configuration_identity plus the existing build_hash and expected_active_build_hash. Its verified response state is configuration-applied and axis_ready must be true. It echoes the accepted configuration_plan and registered active baseline; it must never start the PLC.

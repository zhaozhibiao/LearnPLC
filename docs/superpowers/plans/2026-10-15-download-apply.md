# Download and apply implementation plan

> Execute with subagent-driven-development and independent reviews. User authorizes application workflow changes; no live PLC operations.

**Goal:** Download defaults to applying the compiled program after one core-restart prompt.

**Architecture:** Tested Toolchain workflow with host ports, MainWindow adapter freezing target/candidate and holding operation lock, existing coordinator transactions unchanged. Successful applications restore the confirmed original running/stopped state.

**Tech Stack:** .NET 10, Avalonia; current SSH deployment client.

## Task 1 — Workflow
- [x] Write failing Deployment workflow tests with deterministic fake host and action trace.
- [x] Implement ProgramDownloadRequest, ProgramRestartNotice, ProgramDownloadOutcome/State and IProgramDownloadHost; ProgramDownloadWorkflow.ExecuteAsync and ApplyCompiledAsync.
- [x] Assert compile/readiness gate before prompt; cancel and already-active do not stop/restart; fresh baseline and candidate checks before each write; exact response identities, no retries, verified STOP before restoring a previously running program and final running proof.

## Task 2 — Desktop
- [x] Add actual UI tests for labels, restart prompt and local gates without SSH calls.
- [x] Replace StageAndCompileAsync and ActivateAsync bodies with shared operation-locked adapter; freeze candidate, project and target; disconnect/expire monitor before control changes; update precise result/error status.
- [x] Rename toolbar/page/help, keep secondary apply action, use an adequately sized restart prompt. Run standalone and full desktop tests.

## Task 3 — Verify and deliver
- [x] Build solution, run Deployment and desktop suites; independent spec and quality reviews, fix findings.
- [x] Update download documentation/verification, preserve device-side activation transaction behavior. Commit separately and report restart/use instructions, with no physical deployment performed.

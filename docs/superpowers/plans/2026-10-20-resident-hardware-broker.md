# Resident hardware broker implementation plan

**Goal:** An explicitly authorized ordinary Linux user can apply validated EtherCAT bundles without repeated administrator passwords.

**Architecture:** A root-owned systemd socket/service exposes only status, validate and apply over a local Unix socket. Group permissions plus SO_PEERCRED authorize callers. A fixed root-owned helper retains stopped/disabled checks, immutable binary/config validation and post-start fingerprint proof. The root broker accepts identifiers, never commands or paths. The engineering station continues to use pinned SSH and the deployment coordinator.

**Tech stack:** Python standard library, systemd, existing C#/Avalonia transport. New code MIT. Existing motion core and licenses unchanged.

## Tasks

- [x] Broker protocol tests: reject unknown actions/extra keys/invalid identities/oversize or duplicate JSON; reject unauthorized peers before reading requests; reject repeated apply IDs; verify one request per connection and safe status.
- [x] Implement `linux/ethercat-i210/hardware-broker.py`: bounded framed JSON, kernel peer credentials, fixed root helper import, root-only socket activation, journal audit, no arbitrary subprocess arguments. Existing helper remains responsible for physical safety.
- [x] Deployment client tests and implementation in `linux/deployment/ethercat_hardware.py`: fixed Unix socket, root peer proof, response ID/protocol/build/fingerprint checks, no retries or sudo. Missing/unavailable broker does not stop the axis bridge before preflight succeeds.
- [x] Coordinator strips obsolete password support and carries request ID to broker. Hardware apply remains exclusive under existing deployment and root locks.
- [x] C# UI/transport removes password dialog and credential field, preserves concrete restart confirmation and stale candidate/target checks. Focused agent owns these independent files.
- [x] Add fixed systemd socket/service and pinned installer. Installation grants only `stworkbench-hardware` group membership to account `a`, configures socket mode0660, pins root-owned files, enables boot socket; does not restart motion core or run PLC. No sudoers changes.
- [x] Run unit and build checks; inspect code for privilege/path/protocol/replay failures. Install explicitly approved persistent authorization on 192.168.1.31 via one-time sudo. Test ordinary-user status/validate/denial and one stopped/disabled same-config apply without sudo; no motion or PLC activation.
- [x] Record live evidence, update operator documentation and verify final state/patch whitespace. Do not commit unrelated dirty work.

## Verification commands

`python -m unittest discover -s linux/ethercat-i210 -p 'test_*.py'`

`python -m unittest discover -s linux/deployment -p 'test_*.py'`

`dotnet build StWorkbench.slnx -c Release -p:OutputPath=bin/resident-broker-verify/`

Run the built App and Deployment test executables with `AVALONIA_TELEMETRY_OPTOUT=1`. On Linux use a new ordinary SSH session so the new group membership is effective; verify broker server credentials, socket/service ownership and root-owned source hashes. Read actual PLC status and native snapshot after all operations.

## Verified outcome

2026-10-20: solution build 0 warnings/errors; full App and Deployment executables passed. Python deployment71/71, helper/broker24/24, Linux broker11/11. Independent review's replay-persistence finding fixed and re-reviewed with no remaining serious findings; all installer pins match. Root-owned systemd socket/service installed via explicitly authorized one-time sudo. New SSH session UID1000 includes stworkbench-hardware; socket root:stworkbench-hardware0660, service active and socket boot-enabled. Actual ordinary-user coordinator application succeeded without sudo or credentials; core PID4166 became4844, fingerprint matched, PLCSTOPPED, axisDisabled/faultnull, command sequence0. Same UID/request duplicate rejected and PID unchanged. Live record: examples/generic-ethercat/resident-broker-verification.json. Original PLC active program unchanged and remains incompatible with new hardware until matching download. No motion performed. Initial installation was briefly interrupted by automatic approval quota failure; after user requested continuation, normal approval succeeded and installation completed.

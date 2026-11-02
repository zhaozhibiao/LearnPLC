# Axis dynamics v2 release preparation

This directory contains the pinned release installer and recovery tools. The coordinating session deployed the release on 2026-11-01, verified low-speed ST motion, then restored the original application and RUNNING state. See `docs/axis-dynamics-v2-verification.md` for evidence and limits.

## Evidence and immutable inputs

The reviewed isolated Linux Release binary is `/home/a/st-workbench-i210/axis-dynamics-v2-20261101/native/build/ecmotion-rt`.
Its SHA256 is `f0de2ddd4c943231099b322041471c8748380175229a7582982b08680511a9b4`.
The coordinating agent verified the binary with 22/22 CTest tests and exactly these capabilities:

- `workbench_motion_migration_v1`
- `workbench_axis_trace_v1`
- `workbench_axis_move_profile_v2`
- `workbench_axis_velocity_profile_v2`

The installer embeds reviewed SHA256 constants for that binary and the current apply helper, restore helper, broker, and v2 rollback wrapper. It does not accept replacement digests from the staged release. Refreshing any source requires a new reviewed constant and another test run.

Linux checks: 12 installer/rollback, 38 apply/restore, and 13 broker tests passed. The migration session verified both backup inventories with the installed combined rollback wrapper. Physical commissioning evidence is recorded separately.

## Preparation and installation boundary

Stage exactly `ecmotion-rt`, `apply-runtime-hardware.py`, `restore-runtime.py`, `hardware-broker.py`, and this directory's `rollback.py` under the fixed directory `/home/a/st-workbench-i210/axis-dynamics-v2-release-20261101`. Keep the reviewed installer outside that untrusted input directory when invoking it as root. No staged Python code is imported or executed.

The installer captures and checks every staged file before preflight or publication. Under the existing hardware apply lock it invokes the already root-controlled migration preflight and rollback `--check`, obtaining a fresh torque-disabled, stable-position baseline. It then saves and verifies the existing root broker in a separate supplementary backup.

Only the immutable binary is published before its exact four-capability check. After that check, the installer publishes recovery tooling first, then atomically publishes the apply/restore helpers, broker, and installation pin. Finally it uses `systemctl try-restart stworkbench-hardware.service` to reload the broker. It never starts/stops the native core or PLC, never sends axis commands, and never enables a drive. Installing these files is not activating a v2 program.

## Backup and explicit rollback

The original backup remains unchanged under `/var/backups/st-workbench-runtime/<version>`. Its strict original inventory and old rollback verifier remain valid. The old broker and its checksum, ownership, permissions, and original-backup binding are saved under `/var/backups/st-workbench-axis-dynamics-v2/<same-version>`; no extra files are added to the original backup.

The installed recovery entry point is `/usr/local/libexec/stworkbench-axis-dynamics-v2-rollback`:

```
/usr/local/libexec/stworkbench-axis-dynamics-v2-rollback /var/backups/st-workbench-runtime/<version> --check
/usr/local/libexec/stworkbench-axis-dynamics-v2-rollback /var/backups/st-workbench-runtime/<version> --apply
```

`--check` verifies both backups without restoration. Explicit `--apply` invokes the original rollback helper, which requires native/restore services already stopped; this wrapper does not stop them. It then revalidates and atomically restores the old broker and reloads only the broker. No native/PLC start or drive enable is performed. On failure, inspect both backups and keep native services stopped before retrying. Use the combined wrapper for this release so the broker is restored together with the original runtime inventory.

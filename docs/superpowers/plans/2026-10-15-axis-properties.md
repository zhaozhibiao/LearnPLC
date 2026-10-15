# Axis properties implementation plan

> Execute task by task with subagent-driven-development and review. User requested implementation in the current folder and already approved the fixed-configuration/instruction separation.

**Goal:** Edit validated mechanical, feedback, rotary and homing properties in hardware configuration without pretending they have been applied to a device.

**Architecture:** Optional immutable Mechanics record, pure conversion/validation helper, a focused MainWindow partial for the form, frozen package fields and fail-closed backend entry points. Legacy axes retain null Mechanics and their old serialization.

**Tech Stack:** .NET 10, Avalonia, Python standard library.

## Task 1 — Core model and compatibility
- [x] Write Core tests proving new model/conversion API is absent, then implement AxisMechanics with string enums and finite/range validation.
- [x] Add nullable ignored-when-null AxisConfiguration.Mechanics; integrate validation and reject rotary axes from Cartesian DXF planning even if mislabeled mm.
- [x] Test 131072 counts/rev, 10:1 reduction: 3640.8888889 counts/deg; linear 5 mm/rev gives 262144 counts/mm. Test reciprocal ratios, invalid enums, nonfinite values, homing caps, legacy JSON identity, immutable capture.

## Task 2 — Hardware editor
- [x] Add tests for actual named Avalonia controls selecting rotary, edit roundtrip, invalid-ratio rollback, engineering feedback explanation and save/reopen.
- [x] Implement AxisPropertiesWorkspace.cs with checkbox, grouped labeled form, conversion preview, conditional fields, type/unit defaults; integrate Load/SaveAxisProperties without disturbing IO or library behavior.
- [x] Run full App tests in artifacts/axis-properties-verify to avoid locking the user's running application.

## Task 3 — Frozen packages and unsupported-runtime guard
- [x] Add nullable Mechanics to axis-manifest entries without changing legacy JSON. Verify property freeze in actual generated package.
- [x] Test worker and deployment gate rejecting Mechanics until the controller implements apply/confirm; error must explicitly identify unsupported mechanical/homing configuration. Do not touch running services.
- [x] Build full solution, run Core/Motion/Toolchain/Deployment/App and Python affected tests; review specification then quality; document limits and results.

## Task 4 — Deliver
- [x] Update hardware/axis docs. Commit the verified changes separately from the checkpoint. Report checkpoint/new commit and restart instructions, and distinguish local commits from a remote push.

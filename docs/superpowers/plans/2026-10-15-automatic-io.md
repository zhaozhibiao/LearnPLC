# Automatic IO mapping implementation plan

> Execute with subagent-driven-development; independent Core allocation and UI ownership, then specification and quality review.

**Goal:** Assign missing station PDO mappings safely and expose them as a directly navigable table.

**Architecture:** Pure Core allocation plan, atomic hardware changes, Avalonia native table template preserving existing ListBox contract, tagged tree navigation. No new dependency or device writes.

**Tech Stack:** .NET 10, Avalonia.

## Task 1 — Allocation
- [x] Write failing tests for allocation API, mixed widths, BOOL packing, skipped types and stable existing mappings.
- [x] Implement IoAutoMapping.Plan(HardwareConfiguration, EtherCatStation) returning Added and Skipped; bounded first-free aligned ranges, aliases and case-insensitive names.
- [x] Extend LREAL logical address support to L and test save/validation. Run Core tests, including malformed existing address and exhausted range failure.

## Task 2 — Table and automatic add
- [x] Add failing actual UI tests proving station addition creates rows and table headers are visible.
- [x] Integrate allocation atomically into AddConfiguredStation; add selected-station completion command with counts/skips status and rollback.
- [x] Implement IoMappingWorkspace.cs header/row template with named table; retain IoMappings selection and property editing. Adapt existing configuration test to automatic rows.

## Task 3 — Navigation and verification
- [x] Test left click/repeat click/keyboard navigation to IO table even when IO expander is closed; preserve draft and ST source.
- [x] Tag IO node, implement navigation + content-relative scroll offset; verify selected row edits correct project entry and persists after reopen.
- [x] Run solution build, Core and full desktop tests; independent reviews; document allocation semantics and offline boundary. Commit only feature files after verification.

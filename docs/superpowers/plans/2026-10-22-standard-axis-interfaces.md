# Standard axis interfaces implementation plan

Design: ../specs/2026-10-22-standard-axis-interfaces-design.md (user chose standard MC_* blocks plus brief business sequencing).

- [x] Add compiler-generated failing public-interface tests in tools/axis-bridge; reproduce old error/terminal/read issues.
- [x] Implement per-instance terminal semantics, stop holding and live read/power outputs in tools/axis-bridge/axis_control.st. Preserve ABI and old names; add MC_ReadStatus compatibility alias and synchronized status flag.
- [x] Worker task: invalidate stale payload on terminal worker failure without changing packet layout; test command errors leave good data readable. Files restricted to linux/axis worker/tests.
- [x] Catalog task: synchronize canonical status name, binding detection, output documentation/completion and tests. Keep valid inactive snippets, no output-copy scaffolding. Files restricted to Core catalog/binding/language metadata/tests.
- [x] Build a concise right-library-compatible round-trip example; sequence by Done/Error/CommandAborted, use hardware-owned limits and remove redundant 71xx/arrival/stop verification logic. Save actual .stproj source revisions and compile through MCP.
- [x] Execute compiler-generated public semantics tests, backend worker regression, Core/catalog, relevant UI/MCP builds and example timing tests without hardware access.
- [x] Independent specification review followed by quality review; fix substantive findings and rerun affected checks.
- [x] Update docs with supported public interfaces and migration limits. Report local implementation versus device deployment truthfully.

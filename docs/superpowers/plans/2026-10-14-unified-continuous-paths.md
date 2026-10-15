# Unified continuous path implementation

Approved requirements are in ../specs/2026-10-14-unified-continuous-paths-design.md. Work in current folder, retain existing unrelated edits and existing PLC workflow. Do not modify/run physical controller or start real axes.

1. Add immutable path resource contracts and bounded asset validation; persist assets with project and freeze them into build identity, preserving hashes for projects without paths.
2. Migrate own bounded DXF importer, preserve supported geometry, build a continuous trajectory with explicit constraints and corner tolerance, validate finite bounds and derivatives. Add meaningful virtual tests for line/arc continuity, limits, stop and invalid input. Do not represent numerical/simulator checks as physical certification.
3. Integrate path tree/editor/preview in existing central workspace, project axis mapping, local prestore, versioned assets, dynamic resource library items. No second HMI or independent configuration.
4. Add executable ST path blocks and a Linux nonblocking request/feedback bridge. A separate controller worker owns execution; PLC scan does not parse files or block on IPC. Bind paths by stable ID/version/content identity. Virtual backend must be clearly marked; unsupported physical continuous capability refuses execution.
5. Include assets and bridge dependencies in candidate package, validate dependencies/identity during Linux preparation. Preserve explicit activation/run controls.
6. Build, core/UI/bridge tests, real compiler checks, isolated Linux virtual full chain (without touching active program), review and screenshots. Report actual device capability limits plainly.

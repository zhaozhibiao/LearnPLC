# CSP timing correction execution

Approved design: ../specs/2026-10-29-csp-timing-correction-design.md.
Use test-driven changes and independent review before hardware changes. Work in a separate exact snapshot to preserve existing workspace edits.

- [ ] Read actual synchronization parameters and establish clock/frame timing evidence.
- [ ] Add tested cycle timing/phase controller with bounded corrections and late-cycle handling; extend passive trace.
- [ ] Build independent candidate, run tests, review and verify disabled timing.
- [ ] Measure low-speed delay across directions and phases. Only enable an alignment model if evidence satisfies design gates.
- [ ] Test reference history and layered fault handling with injected faults; preserve raw diagnostics.
- [ ] Repeat staged motion through 300 rpm. Any failure blocks higher stages.
- [ ] Complete 3000 rpm qualification and formal engineering Runtime migration only if gates pass.

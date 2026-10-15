# ST editor live values

Design: reuse the authenticated, identity-checked read-only monitor. Display zero-length visual inlays after lexical variable references; never change source text. The editor provides start/disconnect and explicit instance selection. Resolve program/FB contexts through declarations and authoritative debug paths, never a leaf-name suffix guess. Comments and strings are excluded. Unsupported, stale, disconnected or changed-source values show unavailable.

Implementation: add lexical reference/context resolution and regression cases; add AvaloniaEdit visual generator and editor controls; refresh on samples, navigation, disconnect and edits; build and run offline tests plus the existing current-program read-only Linux verification; capture the ST editor and document usage.

No new dependencies, runtime activation, start/stop or hardware writes.

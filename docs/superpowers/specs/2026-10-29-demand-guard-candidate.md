# Demand-chain protection candidate

Decision: prefer independently monitored command delivery plus conservative drive-local error over unconditional3ms feedback extrapolation. Offline shadow implementation only; NOT deployed and NOT a qualified safety function.

During validated CSP motion, require received6062 to equal actually published target from exactly3 cycles earlier. Enforce consecutive timestamps, valid bus/timing, bounded history. Check max(abs(6062-6064),abs(60F4)) at0.005rev/20ms; retain raw host error separately. This changes the reference used by following-error protection and must not be described as an unchanged algorithm. Preserve overspeed, limits, watchdog, drive faults and timing faults independently.

Seven synthetic tests pass: normal delay, shifted delay, actual position stall, independent drive error, invalid frame, missing cycle, time discontinuity. Actual moving-state replay:12420 low-speed cycles and1271 prior300rpm-attempt cycles; no shadow trip. Conservative peak errors0.003507614rev and0.004006386rev respectively. This suggests the previous host alarm includes command pipeline displacement, not proof of successful300rpm operation.

Important blocker discovered by replay: enabled pre-home6062 can retain previous coordinate demand. Applying chain monitor unconditionally caused false delivery faults. Moving-state-only replay is diagnostic, NOT sufficient coverage for production. Native implementation must explicitly handle reference epoch, Home/Enable/Stop transitions, initial history warmup, sustained standstill, mode change and restart; raw protection must remain active during unvalidated lifecycle phases. The current Python model has no direct device control and cannot replace native protection.

Next required before live activation: native fixed-capacity module, lifecycle/fault injection tests, independent review, shadow-mode live confirmation then guarded30rpm repeat. Only then retry300rpm;3000rpm remains gated. No gain/threshold changes or device motion were performed during this offline selection.

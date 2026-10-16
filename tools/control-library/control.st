(* SPDX-License-Identifier: MIT
   Copyright (c) 2026 ST Workbench contributors
   Workbench control library 1.0.0. Seconds, LREAL, parallel PID gains.
   ErrorID: 1 nonfinite/overflow, 2 invalid sampling/parameter, 3 window.
   A conservative finite envelope of +/- 1E300 is used throughout. *)

FUNCTION WB_FINITE : BOOL
VAR_INPUT Value : LREAL; END_VAR
WB_FINITE := (Value = Value) AND (Value <= 1.0E300) AND (Value >= -1.0E300);
END_FUNCTION

FUNCTION WB_CLAMP : LREAL
VAR_INPUT Value : LREAL; Minimum : LREAL; Maximum : LREAL; END_VAR
IF Value < Minimum THEN WB_CLAMP := Minimum;
ELSIF Value > Maximum THEN WB_CLAMP := Maximum;
ELSE WB_CLAMP := Value;
END_IF;
END_FUNCTION

FUNCTION_BLOCK WB_PID
VAR_INPUT
 Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01;
 Setpoint : LREAL; Measurement : LREAL; Kp : LREAL := 1.0; Ki : LREAL; Kd : LREAL;
 DerivativeTime : LREAL := 0.01; MinOut : LREAL; MaxOut : LREAL := 100.0;
 Manual : BOOL; ManualValue : LREAL; FeedForward : LREAL; Reverse : BOOL;
END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR
 Initialized : BOOL; WasManual : BOOL; Integral : LREAL; PreviousMeasurement : LREAL; Derivative : LREAL;
 E : LREAL; P : LREAL; D : LREAL; I : LREAL; Raw : LREAL; Delta : LREAL; Direction : LREAL;
END_VAR
Error := FALSE; ErrorID := 0;
IF NOT Enable THEN RETURN; END_IF;
IF NOT Initialized AND WB_FINITE(MinOut) AND WB_FINITE(MaxOut) AND (MinOut < MaxOut) THEN Out := WB_CLAMP(0.0, MinOut, MaxOut); END_IF;
IF NOT WB_FINITE(Ts) OR NOT WB_FINITE(Setpoint) OR NOT WB_FINITE(Measurement) OR NOT WB_FINITE(Kp) OR NOT WB_FINITE(Ki) OR NOT WB_FINITE(Kd) OR NOT WB_FINITE(DerivativeTime) OR NOT WB_FINITE(MinOut) OR NOT WB_FINITE(MaxOut) OR NOT WB_FINITE(ManualValue) OR NOT WB_FINITE(FeedForward) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (DerivativeTime < 0.0) OR (MinOut >= MaxOut) OR (Kp < 0.0) OR (Ki < 0.0) OR (Kd < 0.0) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
Direction := 1.0; IF Reverse THEN Direction := -1.0; END_IF;
E := Direction * (Setpoint - Measurement); P := Kp * E;
D := 0.0;
IF Initialized AND NOT Reset THEN
 D := (DerivativeTime * Derivative + Measurement - PreviousMeasurement) / (DerivativeTime + Ts);
END_IF;
I := Integral;
IF NOT Initialized OR Reset THEN I := 0.0; END_IF;
IF Manual THEN
 Raw := WB_CLAMP(ManualValue, MinOut, MaxOut);
 I := Raw - P + Direction * Kd * D - FeedForward;
ELSIF WasManual AND NOT Reset THEN
 I := Out - P + Direction * Kd * D - FeedForward;
 Raw := Out;
ELSE
 Delta := 0.0;
 IF Initialized AND NOT Reset THEN Delta := Ki * E * Ts; END_IF;
 Raw := P + I - Direction * Kd * D + FeedForward;
 IF NOT WB_FINITE(Raw) OR NOT WB_FINITE(Delta) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
 (* Reach the boundary without accumulating beyond it. If P/D already
    saturates output, only accept integral increments toward recovery. *)
 IF (Delta > 0.0) AND (Raw < MaxOut) THEN I := I + MIN(Delta, MaxOut - Raw);
 ELSIF (Delta < 0.0) AND (Raw > MinOut) THEN I := I + MAX(Delta, MinOut - Raw);
 END_IF;
 Raw := P + I - Direction * Kd * D + FeedForward;
END_IF;
IF NOT WB_FINITE(E) OR NOT WB_FINITE(P) OR NOT WB_FINITE(D) OR NOT WB_FINITE(I) OR NOT WB_FINITE(Raw) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := WB_CLAMP(Raw, MinOut, MaxOut);
Integral := I; Derivative := D; PreviousMeasurement := Measurement; Initialized := TRUE; WasManual := Manual;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_INCREMENTAL_PID
VAR_INPUT
 Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01;
 Setpoint : LREAL; Measurement : LREAL; Kp : LREAL := 1.0; Ki : LREAL; Kd : LREAL;
 DerivativeTime : LREAL := 0.01; MinOut : LREAL; MaxOut : LREAL := 100.0;
 Manual : BOOL; ManualValue : LREAL; FeedForward : LREAL; Reverse : BOOL;
END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR
 Initialized : BOOL; WasManual : BOOL; PrevError : LREAL; PrevMeasurement : LREAL;
 PrevDerivative : LREAL; PrevFeedForward : LREAL; E : LREAL; D : LREAL; Raw : LREAL; Direction : LREAL;
END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT Initialized AND WB_FINITE(MinOut) AND WB_FINITE(MaxOut) AND (MinOut < MaxOut) THEN Out := WB_CLAMP(0.0, MinOut, MaxOut); END_IF;
IF NOT WB_FINITE(Ts) OR NOT WB_FINITE(Setpoint) OR NOT WB_FINITE(Measurement) OR NOT WB_FINITE(Kp) OR NOT WB_FINITE(Ki) OR NOT WB_FINITE(Kd) OR NOT WB_FINITE(DerivativeTime) OR NOT WB_FINITE(MinOut) OR NOT WB_FINITE(MaxOut) OR NOT WB_FINITE(ManualValue) OR NOT WB_FINITE(FeedForward) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (DerivativeTime < 0.0) OR (MinOut >= MaxOut) OR (Kp < 0.0) OR (Ki < 0.0) OR (Kd < 0.0) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
Direction := 1.0; IF Reverse THEN Direction := -1.0; END_IF;
E := Direction * (Setpoint - Measurement); D := 0.0;
IF Initialized AND NOT Reset THEN D := (DerivativeTime * PrevDerivative + Measurement - PrevMeasurement) / (DerivativeTime + Ts); END_IF;
IF Manual THEN Raw := ManualValue;
ELSIF NOT Initialized OR Reset THEN Raw := Kp * E + FeedForward;
ELSIF WasManual THEN Raw := Out;
ELSE Raw := Out + Kp * (E - PrevError) + Ki * E * Ts - Direction * Kd * (D - PrevDerivative) + FeedForward - PrevFeedForward;
END_IF;
IF NOT WB_FINITE(E) OR NOT WB_FINITE(D) OR NOT WB_FINITE(Raw) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := WB_CLAMP(Raw, MinOut, MaxOut); PrevError := E; PrevMeasurement := Measurement; PrevDerivative := D; PrevFeedForward := FeedForward; WasManual := Manual; Initialized := TRUE;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_CASCADE_PID
VAR_INPUT
 Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01;
 Setpoint : LREAL; PrimaryMeasurement : LREAL; SecondaryMeasurement : LREAL;
 PrimaryKp : LREAL := 1.0; PrimaryKi : LREAL; PrimaryKd : LREAL; PrimaryDerivativeTime : LREAL := 0.01;
 SecondaryKp : LREAL := 1.0; SecondaryKi : LREAL; SecondaryKd : LREAL; SecondaryDerivativeTime : LREAL := 0.01;
 PrimaryMin : LREAL; PrimaryMax : LREAL := 100.0; MinOut : LREAL; MaxOut : LREAL := 100.0;
 InnerManual : BOOL; ManualValue : LREAL; PrimaryEvery : UINT := 1;
 ReversePrimary : BOOL; ReverseSecondary : BOOL;
END_VAR
VAR_OUTPUT Out : LREAL; SecondarySetpoint : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR
 Outer : WB_PID; Inner : WB_PID; OuterCandidate : WB_PID; InnerCandidate : WB_PID;
 Count : UINT; Elapsed : LREAL; NextElapsed : LREAL; NextSetpoint : LREAL; NextCount : UINT; WasInnerManual : BOOL; Initialized : BOOL;
END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT Initialized AND WB_FINITE(MinOut) AND WB_FINITE(MaxOut) AND (MinOut < MaxOut) THEN Out := WB_CLAMP(0.0, MinOut, MaxOut); END_IF;
IF NOT WB_FINITE(Ts) OR NOT WB_FINITE(PrimaryMeasurement) OR NOT WB_FINITE(SecondaryMeasurement) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (PrimaryEvery < 1) OR (PrimaryEvery > 1000) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
NextElapsed := Elapsed + Ts; NextCount := Count + 1; NextSetpoint := SecondarySetpoint;
OuterCandidate := Outer; InnerCandidate := Inner;
IF Reset OR NOT Initialized THEN NextElapsed := Ts; NextCount := PrimaryEvery; END_IF;
IF (NextCount >= PrimaryEvery) OR InnerManual OR WasInnerManual THEN
 OuterCandidate(Enable := TRUE, Reset := Reset, Ts := NextElapsed, Setpoint := Setpoint, Measurement := PrimaryMeasurement, Kp := PrimaryKp, Ki := PrimaryKi, Kd := PrimaryKd, DerivativeTime := PrimaryDerivativeTime, MinOut := PrimaryMin, MaxOut := PrimaryMax, Manual := InnerManual, ManualValue := SecondaryMeasurement, Reverse := ReversePrimary);
 IF OuterCandidate.Error THEN Error := TRUE; ErrorID := OuterCandidate.ErrorID; RETURN; END_IF;
 NextSetpoint := OuterCandidate.Out; NextCount := 0; NextElapsed := 0.0;
END_IF;
InnerCandidate(Enable := TRUE, Reset := Reset, Ts := Ts, Setpoint := NextSetpoint, Measurement := SecondaryMeasurement, Kp := SecondaryKp, Ki := SecondaryKi, Kd := SecondaryKd, DerivativeTime := SecondaryDerivativeTime, MinOut := MinOut, MaxOut := MaxOut, Manual := InnerManual, ManualValue := ManualValue, Reverse := ReverseSecondary);
IF InnerCandidate.Error OR NOT WB_FINITE(NextElapsed) THEN Error := TRUE; ErrorID := InnerCandidate.ErrorID; IF ErrorID = 0 THEN ErrorID := 1; END_IF; RETURN; END_IF;
Outer := OuterCandidate; Inner := InnerCandidate; Out := Inner.Out; SecondarySetpoint := NextSetpoint; Count := NextCount; Elapsed := NextElapsed; WasInnerManual := InnerManual; Initialized := TRUE;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_LOW_PASS
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; Tau : LREAL := 1.0; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR Initialized : BOOL; Value : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) OR NOT WB_FINITE(Tau) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (Tau < 0.0) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
Value := In;
IF Initialized AND NOT Reset THEN Value := Out + (In - Out) * (Ts / (Tau + Ts)); END_IF;
IF NOT WB_FINITE(Value) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := Value; Initialized := TRUE;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_MOVING_AVERAGE
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; Window : UINT := 8; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; Count : UINT; END_VAR
VAR Buffer : ARRAY[0..63] OF LREAL; Position : UINT; PreviousWindow : UINT; N : UINT; J : UINT; Total : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF Ts <= 0.0 THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
IF (Window < 1) OR (Window > 64) THEN Error := TRUE; ErrorID := 3; RETURN; END_IF;
N := Count; IF Reset OR (PreviousWindow <> Window) THEN N := 0; END_IF;
(* Compute with per-term division to avoid overflow of an otherwise finite mean. *)
IF N < Window THEN N := N + 1; END_IF;
Total := In / UINT_TO_LREAL(N);
IF NOT Reset AND (PreviousWindow = Window) THEN
 FOR J := 0 TO N - 1 DO
  IF (J <> Position) AND (J < Count) THEN Total := Total + Buffer[J] / UINT_TO_LREAL(N); END_IF;
 END_FOR;
END_IF;
IF NOT WB_FINITE(Total) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF Reset OR (PreviousWindow <> Window) THEN Position := 0; END_IF;
Buffer[Position] := In; Position := (Position + 1) MOD Window; Count := N; PreviousWindow := Window; Out := Total;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_MEDIAN
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; Window : UINT := 3; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; Count : UINT; END_VAR
VAR Buffer : ARRAY[0..8] OF LREAL; Sorted : ARRAY[0..8] OF LREAL; Position : UINT; PreviousWindow : UINT; N : UINT; I : UINT; J : UINT; Temp : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF Ts <= 0.0 THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
IF (Window < 1) OR (Window > 9) OR (Window MOD 2 = 0) THEN Error := TRUE; ErrorID := 3; RETURN; END_IF;
IF Reset OR (PreviousWindow <> Window) THEN Count := 0; Position := 0; END_IF;
Buffer[Position] := In; Position := (Position + 1) MOD Window;
IF Count < Window THEN Count := Count + 1; END_IF;
N := Count;
FOR I := 0 TO N - 1 DO Sorted[I] := Buffer[I]; END_FOR;
FOR I := 0 TO N - 1 DO
 FOR J := 0 TO N - 1 DO
  IF Sorted[I] < Sorted[J] THEN Temp := Sorted[I]; Sorted[I] := Sorted[J]; Sorted[J] := Temp; END_IF;
 END_FOR;
END_FOR;
(* During even-count warmup use lower median, without averaging overflow. *)
Out := Sorted[(N - 1) / 2]; PreviousWindow := Window;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_KALMAN_1D
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; Initial : LREAL; InitialCovariance : LREAL := 1.0; ProcessNoise : LREAL := 0.01; MeasurementNoise : LREAL := 1.0; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; Covariance : LREAL; END_VAR
VAR Initialized : BOOL; P : LREAL; Gain : LREAL; X : LREAL; NewP : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) OR NOT WB_FINITE(Initial) OR NOT WB_FINITE(InitialCovariance) OR NOT WB_FINITE(ProcessNoise) OR NOT WB_FINITE(MeasurementNoise) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (InitialCovariance < 0.0) OR (ProcessNoise < 0.0) OR (MeasurementNoise <= 0.0) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
P := Covariance; X := Out; IF NOT Initialized OR Reset THEN P := InitialCovariance; X := Initial; END_IF;
P := P + ProcessNoise * Ts; Gain := P / (P + MeasurementNoise); X := X + Gain * (In - X); NewP := (1.0 - Gain) * P;
IF NOT WB_FINITE(P) OR NOT WB_FINITE(Gain) OR NOT WB_FINITE(X) OR NOT WB_FINITE(NewP) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := X; Covariance := NewP; Initialized := TRUE;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_SLEW_RATE
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; RiseRate : LREAL := 1.0; FallRate : LREAL := 1.0; Initial : LREAL; Track : BOOL; TrackValue : LREAL; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR Initialized : BOOL; Value : LREAL; Delta : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) OR NOT WB_FINITE(RiseRate) OR NOT WB_FINITE(FallRate) OR NOT WB_FINITE(Initial) OR NOT WB_FINITE(TrackValue) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (RiseRate < 0.0) OR (FallRate < 0.0) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
Value := Out; IF NOT Initialized OR Reset THEN Value := Initial; END_IF;
IF Track THEN Value := TrackValue;
ELSIF NOT Reset THEN
 Delta := In - Value;
 IF Delta > 0.0 THEN Value := Value + MIN(Delta, RiseRate * Ts);
 ELSE Value := Value - MIN(-Delta, FallRate * Ts); END_IF;
END_IF;
IF NOT WB_FINITE(Value) OR NOT WB_FINITE(RiseRate * Ts) OR NOT WB_FINITE(FallRate * Ts) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := Value; Initialized := TRUE;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_LEAD_LAG
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; LeadTime : LREAL; LagTime : LREAL := 1.0; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR Initialized : BOOL; PreviousIn : LREAL; Value : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) OR NOT WB_FINITE(LeadTime) OR NOT WB_FINITE(LagTime) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (LeadTime < 0.0) OR (LagTime < 0.0) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
Value := In;
IF Initialized AND NOT Reset THEN Value := (LagTime * Out + Ts * In + LeadTime * (In - PreviousIn)) / (LagTime + Ts); END_IF;
IF NOT WB_FINITE(Value) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := Value; PreviousIn := In; Initialized := TRUE;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_INTEGRATOR
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; Initial : LREAL; Hold : BOOL; MinOut : LREAL := -1.0E100; MaxOut : LREAL := 1.0E100; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR Initialized : BOOL; Value : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT Initialized AND WB_FINITE(MinOut) AND WB_FINITE(MaxOut) AND (MinOut < MaxOut) THEN
 IF WB_FINITE(Initial) THEN Out := WB_CLAMP(Initial, MinOut, MaxOut); ELSE Out := WB_CLAMP(0.0, MinOut, MaxOut); END_IF;
END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) OR NOT WB_FINITE(Initial) OR NOT WB_FINITE(MinOut) OR NOT WB_FINITE(MaxOut) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (MinOut >= MaxOut) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
Value := Out; IF NOT Initialized OR Reset THEN Value := WB_CLAMP(Initial, MinOut, MaxOut); END_IF;
IF NOT Hold AND NOT Reset THEN Value := Value + In * Ts; END_IF;
IF NOT WB_FINITE(Value) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := WB_CLAMP(Value, MinOut, MaxOut); Initialized := TRUE;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_DERIVATIVE
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; FilterTime : LREAL := 0.01; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR Initialized : BOOL; PreviousIn : LREAL; Value : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) OR NOT WB_FINITE(FilterTime) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (FilterTime < 0.0) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
Value := 0.0; IF Initialized AND NOT Reset THEN Value := (FilterTime * Out + In - PreviousIn) / (FilterTime + Ts); END_IF;
IF NOT WB_FINITE(Value) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := Value; PreviousIn := In; Initialized := TRUE;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_SCALE
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; RawMin : LREAL; RawMax : LREAL := 100.0; EngMin : LREAL; EngMax : LREAL := 100.0; Clamp : BOOL := TRUE; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR Ratio : LREAL; Value : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) OR NOT WB_FINITE(RawMin) OR NOT WB_FINITE(RawMax) OR NOT WB_FINITE(EngMin) OR NOT WB_FINITE(EngMax) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (RawMin >= RawMax) OR (EngMin >= EngMax) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
Ratio := (In - RawMin) / (RawMax - RawMin);
IF NOT WB_FINITE(Ratio) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF Clamp THEN Ratio := WB_CLAMP(Ratio, 0.0, 1.0); END_IF;
Value := EngMin + Ratio * (EngMax - EngMin);
IF NOT WB_FINITE(Ratio) OR NOT WB_FINITE(Value) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := Value;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_DEADBAND
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; In : LREAL; Width : LREAL := 1.0; Hysteresis : LREAL; END_VAR
VAR_OUTPUT Out : LREAL; Error : BOOL; ErrorID : UINT; END_VAR
VAR Active : BOOL; NextActive : BOOL; Value : LREAL; Magnitude : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN RETURN; END_IF;
IF NOT WB_FINITE(In) OR NOT WB_FINITE(Ts) OR NOT WB_FINITE(Width) OR NOT WB_FINITE(Hysteresis) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
IF (Ts <= 0.0) OR (Width < 0.0) OR (Hysteresis < 0.0) OR (Hysteresis > Width) THEN Error := TRUE; ErrorID := 2; RETURN; END_IF;
Magnitude := ABS(In); NextActive := Active; IF Reset THEN NextActive := FALSE; END_IF;
IF NextActive THEN IF Magnitude <= Width - Hysteresis THEN NextActive := FALSE; END_IF;
ELSE IF Magnitude > Width + Hysteresis THEN NextActive := TRUE; END_IF; END_IF;
Value := 0.0;
IF NextActive AND (Magnitude > Width) THEN Value := Magnitude - Width; IF In < 0.0 THEN Value := -Value; END_IF; END_IF;
IF NOT WB_FINITE(Value) THEN Error := TRUE; ErrorID := 1; RETURN; END_IF;
Out := Value; Active := NextActive;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_ONOFF
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; Setpoint : LREAL; Measurement : LREAL; Hysteresis : LREAL := 1.0; MinOnTime : LREAL; MinOffTime : LREAL; Reverse : BOOL; FaultOutput : BOOL; END_VAR
VAR_OUTPUT Out : BOOL; Error : BOOL; ErrorID : UINT; END_VAR
VAR Elapsed : LREAL; E : LREAL; NextElapsed : LREAL; Initialized : BOOL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN Out := FALSE; Elapsed := 0.0; Initialized := FALSE; RETURN; END_IF;
IF NOT WB_FINITE(Ts) OR NOT WB_FINITE(Setpoint) OR NOT WB_FINITE(Measurement) OR NOT WB_FINITE(Hysteresis) OR NOT WB_FINITE(MinOnTime) OR NOT WB_FINITE(MinOffTime) THEN Error := TRUE; ErrorID := 1; Out := FaultOutput; Elapsed := 0.0; Initialized := TRUE; RETURN; END_IF;
IF (Ts <= 0.0) OR (Hysteresis < 0.0) OR (MinOnTime < 0.0) OR (MinOffTime < 0.0) THEN Error := TRUE; ErrorID := 2; Out := FaultOutput; Elapsed := 0.0; Initialized := TRUE; RETURN; END_IF;
E := Setpoint - Measurement; IF Reverse THEN E := -E; END_IF;
NextElapsed := Elapsed + Ts;
IF NOT WB_FINITE(E) OR NOT WB_FINITE(NextElapsed) THEN Error := TRUE; ErrorID := 1; Out := FaultOutput; Elapsed := 0.0; Initialized := TRUE; RETURN; END_IF;
IF Reset THEN Out := FALSE; Elapsed := 0.0; Initialized := TRUE; RETURN; END_IF;
IF NOT Initialized THEN NextElapsed := MinOffTime; END_IF;
IF Out THEN
 IF (E < -Hysteresis) AND (NextElapsed >= MinOnTime) THEN Out := FALSE; NextElapsed := 0.0; END_IF;
ELSE IF (E > Hysteresis) AND (NextElapsed >= MinOffTime) THEN Out := TRUE; NextElapsed := 0.0; END_IF; END_IF;
Elapsed := NextElapsed; Initialized := TRUE;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_TIME_PROPORTION
VAR_INPUT Enable : BOOL := TRUE; Reset : BOOL; Ts : LREAL := 0.01; Duty : LREAL; Period : LREAL := 1.0; FaultOutput : BOOL; END_VAR
VAR_OUTPUT Out : BOOL; Error : BOOL; ErrorID : UINT; Phase : LREAL; END_VAR
VAR NextPhase : LREAL; Fraction : LREAL; END_VAR
Error := FALSE; ErrorID := 0; IF NOT Enable THEN Out := FALSE; Phase := 0.0; RETURN; END_IF;
IF NOT WB_FINITE(Ts) OR NOT WB_FINITE(Duty) OR NOT WB_FINITE(Period) THEN Error := TRUE; ErrorID := 1; Out := FaultOutput; RETURN; END_IF;
IF (Ts <= 0.0) OR (Period <= 0.0) OR (Ts > Period) THEN Error := TRUE; ErrorID := 2; Out := FaultOutput; RETURN; END_IF;
IF Reset THEN Phase := 0.0; END_IF;
(* Phase is normalized [0,1); bounded one subtraction because Ts <= Period. *)
NextPhase := Phase; IF NextPhase >= 1.0 THEN NextPhase := NextPhase - 1.0; END_IF;
Fraction := WB_CLAMP(Duty, 0.0, 1.0);
Out := NextPhase < Fraction; Phase := NextPhase + Ts / Period;
IF Phase >= 1.0 THEN Phase := Phase - 1.0; END_IF;
END_FUNCTION_BLOCK

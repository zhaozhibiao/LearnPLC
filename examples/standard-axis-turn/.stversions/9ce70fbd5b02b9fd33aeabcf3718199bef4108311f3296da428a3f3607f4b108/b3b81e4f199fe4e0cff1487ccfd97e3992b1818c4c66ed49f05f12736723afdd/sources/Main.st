(* SPDX-License-Identifier: MIT
   Engineering axis X -> controller Axis1; all positions/speeds are in rev.
   Motion blocks are called every PLC scan. Mechanical and homing parameters
   belong to hardware configuration, never to this program. *)
PROGRAM Main
VAR
    (* Operator inputs: all commands default FALSE. *)
    PowerEnable : BOOL := FALSE;
    HomeExecute : BOOL := FALSE;
    CycleExecute : BOOL := FALSE;
    StopExecute : BOOL := FALSE;
    ResetExecute : BOOL := FALSE;
    Acknowledge : BOOL := FALSE;
    ForwardTurns : LREAL := 1.5;
    ReverseTurns : LREAL := 1.0;
    Speed : LREAL := 0.01;

    (* Operator feedback. Done remains set until acknowledged. *)
    Ready : BOOL;
    CycleActive : BOOL;
    Done : BOOL;
    Failed : BOOL;
    Cancelled : BOOL;
    OutcomeUnknown : BOOL;
    DiagnosticCode : UDINT;
    AxisErrorID : UDINT;
    StopDiagnosticCode : UDINT;
    StopErrorID : UDINT;
    Step : UINT := 0;
    ActualPosition : LREAL;
    ActualVelocity : LREAL;

    (* Standard engineering-axis FB instances. *)
    AxisPower : MC_Power;
    AxisHome : MC_Home;
    AxisReset : MC_Reset;
    ForwardMove : MC_MoveAbsolute;
    ReverseMove : MC_MoveAbsolute;
    AxisStop : MC_Stop;
    AxisStatus : MC_ReadAxisStatus;
    AxisPosition : MC_ReadActualPosition;
    AxisVelocity : MC_ReadActualVelocity;
    Dwell : TON;
    CycleTimeout : TON;
    StopTimeout : TON;

    PreviousCycle : BOOL;
    CycleRise : BOOL;
    StartPosition : LREAL;
    ForwardTarget : LREAL;
    ReverseTarget : LREAL;
    AcceptedSpeed : LREAL;
END_VAR

AxisStatus(Axis := AXIS_X, Enable := TRUE);
AxisPosition(Axis := AXIS_X, Enable := TRUE);
AxisVelocity(Axis := AXIS_X, Enable := TRUE);
ActualPosition := AxisPosition.Position;
ActualVelocity := AxisVelocity.Velocity;
Ready := PowerEnable AND AxisPower.Status AND NOT AxisPower.Busy
    AND AxisStatus.Valid AND AxisPosition.Valid AND AxisVelocity.Valid
    AND AxisStatus.Homed AND AxisStatus.Standstill
    AND NOT AxisStatus.ErrorStop AND NOT AxisHome.Busy
    AND NOT HomeExecute AND NOT ResetExecute AND NOT StopExecute
    AND NOT CycleActive AND NOT Failed AND NOT OutcomeUnknown AND (Step = 0)
    AND (ABS(ActualVelocity) <= 0.001);
CycleRise := CycleExecute AND NOT PreviousCycle;
PreviousCycle := CycleExecute;

IF Acknowledge AND NOT CycleExecute AND NOT CycleActive AND NOT StopExecute THEN
    Step := 0; Done := FALSE; Failed := FALSE; Cancelled := FALSE;
    OutcomeUnknown := FALSE; DiagnosticCode := 0;
    AxisErrorID := 0; StopDiagnosticCode := 0; StopErrorID := 0;
END_IF;

IF CycleRise AND (Step = 0) THEN
    Done := FALSE; Cancelled := FALSE; DiagnosticCode := 0;
    AxisErrorID := 0; StopDiagnosticCode := 0; StopErrorID := 0;
    IF NOT Ready THEN
        Failed := TRUE; DiagnosticCode := 7103; Step := 900;
    ELSIF (ForwardTurns > 0.0) AND (ForwardTurns <= 1.5)
        AND (ReverseTurns > 0.0) AND (ReverseTurns <= 1.0)
        AND (Speed > 0.0) AND (Speed <= 0.01)
        AND (ActualPosition >= -2.0) AND (ActualPosition <= 2.0)
        AND (ActualPosition + ForwardTurns >= -2.0)
        AND (ActualPosition + ForwardTurns <= 2.0)
        AND (ActualPosition + ForwardTurns - ReverseTurns >= -2.0)
        AND (ActualPosition + ForwardTurns - ReverseTurns <= 2.0) THEN
        StartPosition := ActualPosition;
        ForwardTarget := StartPosition + ForwardTurns;
        ReverseTarget := ForwardTarget - ReverseTurns;
        AcceptedSpeed := Speed;
        CycleActive := TRUE; Step := 10;
    ELSE
        Failed := TRUE; DiagnosticCode := 7105; Step := 900;
    END_IF;
END_IF;

Dwell(IN := Step = 20, PT := T#2s);
CycleTimeout(IN := CycleActive, PT := T#300s);
StopTimeout(IN := Step = 80, PT := T#2s);
IF CycleActive AND (Step < 80) THEN
    IF StopExecute OR NOT PowerEnable THEN
        Cancelled := TRUE; Step := 80;
    ELSIF CycleTimeout.Q THEN
        Failed := TRUE; DiagnosticCode := 7111; Step := 80;
    ELSIF NOT AxisStatus.Valid OR NOT AxisPosition.Valid OR NOT AxisVelocity.Valid
        OR AxisStatus.ErrorStop THEN
        Failed := TRUE; DiagnosticCode := 7112; Step := 80;
        AxisErrorID := AxisStatus.ErrorID;
        IF AxisErrorID = 0 THEN AxisErrorID := AxisPosition.ErrorID; END_IF;
        IF AxisErrorID = 0 THEN AxisErrorID := AxisVelocity.ErrorID; END_IF;
    END_IF;
END_IF;

(* Cyclic calls: Execute selects the operation; calls are never inside CASE. *)
AxisStop(Axis := AXIS_X, Execute := StopExecute OR (Step = 80));
AxisPower(Axis := AXIS_X, Enable := PowerEnable);
AxisReset(Axis := AXIS_X, Execute := ResetExecute AND NOT CycleActive);
AxisHome(Axis := AXIS_X, Execute := HomeExecute AND NOT CycleActive
    AND AxisPower.Status AND NOT AxisPower.Busy AND NOT StopExecute);
ForwardMove(Axis := AXIS_X, Execute := Step = 10,
    Position := ForwardTarget, Velocity := AcceptedSpeed);
ReverseMove(Axis := AXIS_X, Execute := Step = 30,
    Position := ReverseTarget, Velocity := AcceptedSpeed);

IF CycleActive AND (Step < 80) THEN
    IF AxisPower.Error OR ForwardMove.Error OR ReverseMove.Error
        OR ((Step = 10) AND ForwardMove.CommandAborted)
        OR ((Step = 30) AND ReverseMove.CommandAborted) THEN
        Failed := TRUE; DiagnosticCode := 7114; Step := 80;
        AxisErrorID := AxisPower.ErrorID;
        IF AxisErrorID = 0 THEN AxisErrorID := ForwardMove.ErrorID; END_IF;
        IF AxisErrorID = 0 THEN AxisErrorID := ReverseMove.ErrorID; END_IF;
    END_IF;
END_IF;

CASE Step OF
10:
    IF ForwardMove.Done AND AxisPosition.Valid
        AND AxisStatus.Standstill
        AND (ABS(ActualPosition - ForwardTarget) <= 0.0001) THEN Step := 20; END_IF;
20:
    IF Dwell.Q THEN Step := 30; END_IF;
30:
    IF ReverseMove.Done AND AxisPosition.Valid
        AND AxisStatus.Standstill
        AND (ABS(ActualPosition - ReverseTarget) <= 0.0001) THEN
        CycleActive := FALSE; Done := TRUE; Step := 100;
    END_IF;
80:
    IF AxisStatus.Valid AND AxisVelocity.Valid
        AND (ABS(ActualVelocity) <= 0.001)
        AND (AxisStop.Done OR AxisStatus.Disabled) THEN
        CycleActive := FALSE;
        IF Failed THEN Step := 900; ELSE Step := 100; END_IF;
    ELSIF AxisStop.Error OR StopTimeout.Q THEN
        CycleActive := FALSE; Failed := TRUE; OutcomeUnknown := TRUE;
        (* Preserve the initiating fault; failed stop confirmation is secondary. *)
        StopDiagnosticCode := 7115; StopErrorID := AxisStop.ErrorID;
        IF DiagnosticCode = 0 THEN DiagnosticCode := 7115; END_IF;
        Step := 900;
    END_IF;
END_CASE;
END_PROGRAM

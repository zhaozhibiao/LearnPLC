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
    Speed : LREAL := 0.5; (* rev/s: 30 rpm; engineering command ceiling is 1 rev/s. *)

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

    (* Instances and calls inserted through the actual right-side library catalog. *)
    CycleRise : BOOL;
    StartPosition : LREAL;
    ForwardTarget : LREAL;
    ReverseTarget : LREAL;
    AcceptedSpeed : LREAL;
    SnippetAxis_X_MC_ReadAxisStatus1 : MC_ReadAxisStatus;
    SnippetAxis_X_MC_ReadActualPosition1 : MC_ReadActualPosition;
    SnippetAxis_X_MC_ReadActualVelocity1 : MC_ReadActualVelocity;
    SnippetAxis_X_MC_Stop1 : MC_Stop;
    SnippetAxis_X_MC_Stop1Execute : BOOL := FALSE;
    SnippetAxis_X_MC_Power1 : MC_Power;
    SnippetAxis_X_MC_Power1Enable : BOOL := FALSE;
    SnippetAxis_X_MC_Reset1 : MC_Reset;
    SnippetAxis_X_MC_Reset1Execute : BOOL := FALSE;
    SnippetAxis_X_MC_Home1 : MC_Home;
    SnippetAxis_X_MC_Home1Execute : BOOL := FALSE;
    SnippetAxis_X_MC_MoveAbsolute1 : MC_MoveAbsolute;
    SnippetAxis_X_MC_MoveAbsolute1Execute : BOOL := FALSE;
    SnippetAxis_X_MC_MoveAbsolute1Position : LREAL := 0.0;
    SnippetAxis_X_MC_MoveAbsolute1Velocity : LREAL := 1.0;
    SnippetAxis_X_MC_MoveAbsolute2 : MC_MoveAbsolute;
    SnippetAxis_X_MC_MoveAbsolute2Execute : BOOL := FALSE;
    SnippetAxis_X_MC_MoveAbsolute2Position : LREAL := 0.0;
    SnippetAxis_X_MC_MoveAbsolute2Velocity : LREAL := 1.0;
    SnippetTON1 : TON;
    SnippetTON1Input : BOOL := FALSE;
    SnippetTON2 : TON;
    SnippetTON2Input : BOOL := FALSE;
    SnippetTON3 : TON;
    SnippetTON3Input : BOOL := FALSE;
    SnippetR_TRIG1 : R_TRIG;
    SnippetR_TRIG1Signal : BOOL := FALSE;
END_VAR

SnippetAxis_X_MC_ReadAxisStatus1(Axis := AXIS_X, Enable := TRUE);
SnippetAxis_X_MC_ReadActualPosition1(Axis := AXIS_X, Enable := TRUE);
SnippetAxis_X_MC_ReadActualVelocity1(Axis := AXIS_X, Enable := TRUE);
ActualPosition := SnippetAxis_X_MC_ReadActualPosition1.Position;
ActualVelocity := SnippetAxis_X_MC_ReadActualVelocity1.Velocity;
Ready := PowerEnable AND SnippetAxis_X_MC_Power1.Status AND NOT SnippetAxis_X_MC_Power1.Busy
    AND SnippetAxis_X_MC_ReadAxisStatus1.Valid AND SnippetAxis_X_MC_ReadActualPosition1.Valid AND SnippetAxis_X_MC_ReadActualVelocity1.Valid
    AND SnippetAxis_X_MC_ReadAxisStatus1.Homed AND SnippetAxis_X_MC_ReadAxisStatus1.Standstill
    AND NOT SnippetAxis_X_MC_ReadAxisStatus1.ErrorStop AND NOT SnippetAxis_X_MC_Home1.Busy
    AND NOT HomeExecute AND NOT ResetExecute AND NOT StopExecute
    AND NOT CycleActive AND NOT Failed AND NOT OutcomeUnknown AND (Step = 0)
    AND (ABS(ActualVelocity) <= 0.001);
SnippetR_TRIG1Signal := CycleExecute;
SnippetR_TRIG1(CLK := SnippetR_TRIG1Signal);
CycleRise := SnippetR_TRIG1.Q;

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
        AND (Speed > 0.0) AND (Speed <= 1.0)
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

SnippetTON1Input := Step = 20;
SnippetTON1(IN := SnippetTON1Input, PT := T#2s);
SnippetTON2Input := CycleActive;
SnippetTON2(IN := SnippetTON2Input, PT := T#300s);
SnippetTON3Input := Step = 80;
SnippetTON3(IN := SnippetTON3Input, PT := T#2s);
IF CycleActive AND (Step < 80) THEN
    IF StopExecute OR NOT PowerEnable THEN
        Cancelled := TRUE; Step := 80;
    ELSIF SnippetTON2.Q THEN
        Failed := TRUE; DiagnosticCode := 7111; Step := 80;
    ELSIF NOT SnippetAxis_X_MC_ReadAxisStatus1.Valid OR NOT SnippetAxis_X_MC_ReadActualPosition1.Valid OR NOT SnippetAxis_X_MC_ReadActualVelocity1.Valid
        OR SnippetAxis_X_MC_ReadAxisStatus1.ErrorStop THEN
        Failed := TRUE; DiagnosticCode := 7112; Step := 80;
        AxisErrorID := SnippetAxis_X_MC_ReadAxisStatus1.ErrorID;
        IF AxisErrorID = 0 THEN AxisErrorID := SnippetAxis_X_MC_ReadActualPosition1.ErrorID; END_IF;
        IF AxisErrorID = 0 THEN AxisErrorID := SnippetAxis_X_MC_ReadActualVelocity1.ErrorID; END_IF;
    END_IF;
END_IF;

(* Cyclic calls: Execute selects the operation; calls are never inside CASE. *)
SnippetAxis_X_MC_Stop1Execute := StopExecute OR (Step = 80);
SnippetAxis_X_MC_Stop1(Axis := AXIS_X, Execute := SnippetAxis_X_MC_Stop1Execute);
SnippetAxis_X_MC_Power1Enable := PowerEnable;
SnippetAxis_X_MC_Power1(Axis := AXIS_X, Enable := SnippetAxis_X_MC_Power1Enable);
SnippetAxis_X_MC_Reset1Execute := ResetExecute AND NOT CycleActive;
SnippetAxis_X_MC_Reset1(Axis := AXIS_X, Execute := SnippetAxis_X_MC_Reset1Execute);
SnippetAxis_X_MC_Home1Execute := HomeExecute AND NOT CycleActive AND SnippetAxis_X_MC_Power1.Status AND NOT SnippetAxis_X_MC_Power1.Busy AND NOT StopExecute;
SnippetAxis_X_MC_Home1(Axis := AXIS_X, Execute := SnippetAxis_X_MC_Home1Execute);
SnippetAxis_X_MC_MoveAbsolute1Execute := Step = 10;
SnippetAxis_X_MC_MoveAbsolute1Position := ForwardTarget;
SnippetAxis_X_MC_MoveAbsolute1Velocity := AcceptedSpeed;
SnippetAxis_X_MC_MoveAbsolute1(Axis := AXIS_X, Execute := SnippetAxis_X_MC_MoveAbsolute1Execute, Position := SnippetAxis_X_MC_MoveAbsolute1Position, Velocity := SnippetAxis_X_MC_MoveAbsolute1Velocity);
SnippetAxis_X_MC_MoveAbsolute2Execute := Step = 30;
SnippetAxis_X_MC_MoveAbsolute2Position := ReverseTarget;
SnippetAxis_X_MC_MoveAbsolute2Velocity := AcceptedSpeed;
SnippetAxis_X_MC_MoveAbsolute2(Axis := AXIS_X, Execute := SnippetAxis_X_MC_MoveAbsolute2Execute, Position := SnippetAxis_X_MC_MoveAbsolute2Position, Velocity := SnippetAxis_X_MC_MoveAbsolute2Velocity);

IF CycleActive AND (Step < 80) THEN
    IF SnippetAxis_X_MC_Power1.Error OR SnippetAxis_X_MC_MoveAbsolute1.Error OR SnippetAxis_X_MC_MoveAbsolute2.Error
        OR ((Step = 10) AND SnippetAxis_X_MC_MoveAbsolute1.CommandAborted)
        OR ((Step = 30) AND SnippetAxis_X_MC_MoveAbsolute2.CommandAborted) THEN
        Failed := TRUE; DiagnosticCode := 7114; Step := 80;
        AxisErrorID := SnippetAxis_X_MC_Power1.ErrorID;
        IF AxisErrorID = 0 THEN AxisErrorID := SnippetAxis_X_MC_MoveAbsolute1.ErrorID; END_IF;
        IF AxisErrorID = 0 THEN AxisErrorID := SnippetAxis_X_MC_MoveAbsolute2.ErrorID; END_IF;
    END_IF;
END_IF;

CASE Step OF
10:
    IF SnippetAxis_X_MC_MoveAbsolute1.Done AND SnippetAxis_X_MC_ReadActualPosition1.Valid
        AND SnippetAxis_X_MC_ReadAxisStatus1.Standstill
        AND (ABS(ActualPosition - ForwardTarget) <= 0.0001) THEN Step := 20; END_IF;
20:
    IF SnippetTON1.Q THEN Step := 30; END_IF;
30:
    IF SnippetAxis_X_MC_MoveAbsolute2.Done AND SnippetAxis_X_MC_ReadActualPosition1.Valid
        AND SnippetAxis_X_MC_ReadAxisStatus1.Standstill
        AND (ABS(ActualPosition - ReverseTarget) <= 0.0001) THEN
        CycleActive := FALSE; Done := TRUE; Step := 100;
    END_IF;
80:
    IF SnippetAxis_X_MC_ReadAxisStatus1.Valid AND SnippetAxis_X_MC_ReadActualVelocity1.Valid
        AND (ABS(ActualVelocity) <= 0.001)
        AND (SnippetAxis_X_MC_Stop1.Done OR SnippetAxis_X_MC_ReadAxisStatus1.Disabled) THEN
        CycleActive := FALSE;
        IF Failed THEN Step := 900; ELSE Step := 100; END_IF;
    ELSIF SnippetAxis_X_MC_Stop1.Error OR SnippetTON3.Q THEN
        CycleActive := FALSE; Failed := TRUE; OutcomeUnknown := TRUE;
        (* Preserve the initiating fault; failed stop confirmation is secondary. *)
        StopDiagnosticCode := 7115; StopErrorID := SnippetAxis_X_MC_Stop1.ErrorID;
        IF DiagnosticCode = 0 THEN DiagnosticCode := 7115; END_IF;
        Step := 900;
    END_IF;
END_CASE;
END_PROGRAM

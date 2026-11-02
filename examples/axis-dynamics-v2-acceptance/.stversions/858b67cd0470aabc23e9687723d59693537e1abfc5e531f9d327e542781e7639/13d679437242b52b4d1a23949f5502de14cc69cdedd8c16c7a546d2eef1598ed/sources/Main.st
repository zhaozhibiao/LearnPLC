(* MIT. Standard MC_* interfaces; hardware owns limits and completion policy. *)
PROGRAM Main
VAR
    PowerEnable : BOOL := FALSE;
    HomeExecute : BOOL := FALSE;
    CycleExecute : BOOL := FALSE;
    StopExecute : BOOL := FALSE;
    ResetExecute : BOOL := FALSE;
    ForwardTurns : LREAL := 0.01;
    ReverseTurns : LREAL := 0.01;
    Speed : LREAL := 0.05; (* dedicated unloaded 3000 rpm acceptance cap *)
    Step : UINT := 0;
    StartPosition : LREAL;
    ErrorID : UDINT;
    Done : BOOL;
    Cancelled : BOOL;
    Power : MC_Power;
    Home : MC_Home;
    Reset : MC_Reset;
    Status : MC_ReadStatus;
    Position : MC_ReadActualPosition;
    ForwardMove : MC_MoveAbsolute;
    ReverseMove : MC_MoveAbsolute;
    Stop : MC_Stop;
    Dwell : TON;
    StartEdge : R_TRIG;
END_VAR

(* Every block is called every scan; command inputs start FALSE. *)
Power(Axis := AXIS_X, Enable := PowerEnable);
Home(Axis := AXIS_X, Execute := HomeExecute);
Reset(Axis := AXIS_X, Execute := ResetExecute);
Status(Axis := AXIS_X, Enable := TRUE);
Position(Axis := AXIS_X, Enable := TRUE);
StartEdge(CLK := CycleExecute);

IF StartEdge.Q AND (Step = 0) AND Position.Valid AND Status.Standstill AND Status.Homed THEN
    StartPosition := Position.Position;
    ErrorID := 0; Cancelled := FALSE; Step := 10;
END_IF;
IF StopExecute AND ((Step = 10) OR (Step = 20) OR (Step = 30)) THEN
    Cancelled := TRUE; Step := 80;
END_IF;

Stop(Axis := AXIS_X, Execute := StopExecute OR (Step = 80), Deceleration := 0.2, Jerk := 1.0);
ForwardMove(Axis := AXIS_X, Execute := Step = 10,
    Position := StartPosition + ForwardTurns, Velocity := Speed, Acceleration := 0.2, Deceleration := 0.2, Jerk := 1.0);
Dwell(IN := Step = 20, PT := T#2s);
ReverseMove(Axis := AXIS_X, Execute := Step = 30,
    Position := StartPosition + ForwardTurns - ReverseTurns, Velocity := Speed, Acceleration := 0.2, Deceleration := 0.2, Jerk := 1.0);

(* Business sequence only: no duplicate position/velocity tolerances or 71xx errors. *)
CASE Step OF
10:
    IF ForwardMove.Done THEN Step := 20;
    ELSIF ForwardMove.Error THEN ErrorID := ForwardMove.ErrorID; Step := 900;
    ELSIF ForwardMove.CommandAborted THEN Cancelled := TRUE; Step := 100; END_IF;
20:
    IF Dwell.Q THEN Step := 30; END_IF;
30:
    IF ReverseMove.Done THEN Step := 100;
    ELSIF ReverseMove.Error THEN ErrorID := ReverseMove.ErrorID; Step := 900;
    ELSIF ReverseMove.CommandAborted THEN Cancelled := TRUE; Step := 100; END_IF;
80:
    IF Stop.Done THEN Step := 100;
    ELSIF Stop.Error THEN
        IF ErrorID = 0 THEN ErrorID := Stop.ErrorID; END_IF;
        Step := 900;
    END_IF;
100:
    IF NOT CycleExecute AND NOT StopExecute THEN Step := 0; END_IF;
900:
    IF Reset.Done AND NOT CycleExecute AND NOT StopExecute THEN ErrorID := 0; Step := 0; END_IF;
END_CASE;
Done := (Step = 100) AND NOT Cancelled;
END_PROGRAM

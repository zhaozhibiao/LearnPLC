(* MIT. Standard MC_* native closed-loop test sequence on configured axis AXIS_X *)
PROGRAM Main
VAR
    PowerEnable : BOOL := FALSE;
    StartExecute : BOOL := FALSE;
    JogExecute : BOOL := FALSE;
    StopExecute : BOOL := FALSE;
    ResetExecute : BOOL := FALSE;

    (* Target parameters within configured limits: MaxVel 50, MaxAcc 5, MaxDec 5, MaxJerk 20 *)
    ForwardTurns : LREAL := 2.0;
    ReverseTurns : LREAL := 2.0;
    Speed : LREAL := 1.0;
    Acc : LREAL := 2.0;
    Dec : LREAL := 2.0;
    JerkVal : LREAL := 10.0;
    JogSpeed : LREAL := 0.5;

    Step : UINT := 0;
    StartPosition : LREAL := 0.0;
    ErrorID : UDINT := 0;
    Done : BOOL := FALSE;
    Cancelled : BOOL := FALSE;

    (* Native Axis Function Block Instances *)
    Power : MC_Power;
    Home : MC_Home;
    Reset : MC_Reset;
    Status : MC_ReadStatus;
    Position : MC_ReadActualPosition;
    MoveFwd : MC_MoveAbsolute;
    MoveRev : MC_MoveAbsolute;
    Jog : MC_MoveVelocity;
    Stop : MC_Stop;

    (* Timers & Triggers *)
    Dwell : TON;
    JogTimer : TON;
    StartEdge : R_TRIG;
END_VAR

(* Continuous state & position feedback read on AXIS_X every scan *)
Status(Axis := AXIS_X, Enable := TRUE);
Position(Axis := AXIS_X, Enable := TRUE);
StartEdge(CLK := StartExecute);

(* Commands called cyclically with parameter bindings *)
Power(Axis := AXIS_X, Enable := PowerEnable);
Reset(Axis := AXIS_X, Execute := ResetExecute);
Home(Axis := AXIS_X, Execute := (Step = 10));

MoveFwd(Axis := AXIS_X, Execute := (Step = 20),
    Position := StartPosition + ForwardTurns,
    Velocity := Speed, Acceleration := Acc, Deceleration := Dec, Jerk := JerkVal);

Dwell(IN := (Step = 30), PT := T#1s);

MoveRev(Axis := AXIS_X, Execute := (Step = 40),
    Position := StartPosition + ForwardTurns - ReverseTurns,
    Velocity := Speed, Acceleration := Acc, Deceleration := Dec, Jerk := JerkVal);

Jog(Axis := AXIS_X, Execute := (Step = 50) OR JogExecute,
    Velocity := JogSpeed, Acceleration := Acc, Deceleration := Dec, Jerk := JerkVal);

JogTimer(IN := (Step = 50), PT := T#1500ms);

Stop(Axis := AXIS_X, Execute := StopExecute OR (Step = 60),
    Deceleration := Dec, Jerk := JerkVal);

(* Sequence State Machine *)
IF StopExecute AND (Step > 0) AND (Step < 100) THEN
    Cancelled := TRUE;
    Step := 60;
END_IF;

CASE Step OF
0: (* Idle wait for Start trigger when powered and stand still *)
    IF StartEdge.Q AND Power.Status AND Status.Standstill THEN
        StartPosition := Position.Position;
        ErrorID := 0;
        Cancelled := FALSE;
        Done := FALSE;
        IF Status.Homed THEN
            Step := 20; (* If already homed, proceed directly to forward move *)
        ELSE
            Step := 10; (* Otherwise perform homing *)
        END_IF;
    END_IF;

10: (* Homing *)
    IF Home.Done THEN
        StartPosition := Position.Position;
        Step := 20;
    ELSIF Home.Error THEN
        ErrorID := Home.ErrorID;
        Step := 900;
    ELSIF Home.CommandAborted THEN
        Cancelled := TRUE;
        Step := 100;
    END_IF;

20: (* Forward Absolute Move with Jerk *)
    IF MoveFwd.Done THEN
        Step := 30;
    ELSIF MoveFwd.Error THEN
        ErrorID := MoveFwd.ErrorID;
        Step := 900;
    ELSIF MoveFwd.CommandAborted THEN
        Cancelled := TRUE;
        Step := 100;
    END_IF;

30: (* Dwell 1s *)
    IF Dwell.Q THEN
        Step := 40;
    END_IF;

40: (* Reverse Absolute Move back to start with Jerk *)
    IF MoveRev.Done THEN
        Step := 50;
    ELSIF MoveRev.Error THEN
        ErrorID := MoveRev.ErrorID;
        Step := 900;
    ELSIF MoveRev.CommandAborted THEN
        Cancelled := TRUE;
        Step := 100;
    END_IF;

50: (* Jog / Velocity motion for 1.5s *)
    IF JogTimer.Q THEN
        Step := 60; (* Trigger controlled stop *)
    ELSIF Jog.Error THEN
        ErrorID := Jog.ErrorID;
        Step := 900;
    ELSIF Jog.CommandAborted THEN
        Cancelled := TRUE;
        Step := 100;
    END_IF;

60: (* Controlled Stop *)
    IF Stop.Done THEN
        Step := 100;
    ELSIF Stop.Error THEN
        IF ErrorID = 0 THEN ErrorID := Stop.ErrorID; END_IF;
        Step := 900;
    END_IF;

100: (* Sequence completed *)
    Done := NOT Cancelled;
    IF NOT StartExecute AND NOT StopExecute THEN
        Step := 0;
    END_IF;

900: (* Error state, wait for Reset *)
    IF Reset.Done AND NOT StartExecute AND NOT StopExecute THEN
        ErrorID := 0;
        Step := 0;
    END_IF;
END_CASE;
END_PROGRAM
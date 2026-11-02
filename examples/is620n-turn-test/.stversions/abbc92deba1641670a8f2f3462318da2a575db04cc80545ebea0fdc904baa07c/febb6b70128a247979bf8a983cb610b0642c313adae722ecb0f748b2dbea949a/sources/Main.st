PROGRAM Main
VAR
    (* Single unloaded-motor test: +1.5 rev, dwell 2 seconds, then -1 rev.
       Ends at start +0.5 rev, not the original start.
       Apply the matching +/-2 rev engineering configuration before any real run.
       RunOnce and ParametersConfirmed default FALSE: no cold-start commands.
       Temporary-zero homing uses the already commissioned method-35 profile. *)
    RunOnce : BOOL := FALSE;
    ParametersConfirmed : BOOL := FALSE;
    EstablishTemporaryZero : BOOL := TRUE;
    CancelTest : BOOL := FALSE;
    Acknowledge : BOOL := FALSE;
    DistanceRev : LREAL := 1.5;
    ReverseDistanceRev : LREAL := 1.0;
    VelocityRevPerSecond : LREAL := 0.01;
    PositionToleranceRev : LREAL := 0.0001;
    State : UINT := 0;
    Active : BOOL;
    Passed : BOOL;
    Failed : BOOL;
    OutcomeUnknown : BOOL;
    DiagnosticCode : UDINT;
    ReadyForTest : BOOL;
    NeedsReference : BOOL;
    ActualPositionRev : LREAL;
    ActualVelocityRevPerSecond : LREAL;
    StartPositionRev : LREAL;
    OutwardPositionRev : LREAL;
    ReturnPositionRev : LREAL;
    ForwardDistanceCommand : LREAL;
    ReverseDistanceCommand : LREAL;
    VelocityCommand : LREAL;
    ToleranceCommand : LREAL;
    HomeRequested : BOOL;
    PlannedStartRev : LREAL;
    CompletedTests : UINT;
    PreviousRun : BOOL;
    StartEdge : BOOL;
    PowerDemand : BOOL;
    SequenceCompleted : BOOL;
    ReadStatus : MC_ReadAxisStatus;
    ReadPosition : MC_ReadActualPosition;
    ReadVelocity : MC_ReadActualVelocity;
    Power : MC_Power;
    Home : MC_Home;
    MoveOut : MC_MoveAbsolute;
    MoveBack : MC_MoveAbsolute;
    StopAxis : MC_Stop;
    TotalTimer : TON;
    DwellTimer : TON;
    StopTimer : TON;
    DisableTimer : TON;
END_VAR

ReadStatus(Axis := AXIS_IS620N, Enable := TRUE);
ReadPosition(Axis := AXIS_IS620N, Enable := TRUE);
ReadVelocity(Axis := AXIS_IS620N, Enable := TRUE);
ActualPositionRev := ReadPosition.Position;
ActualVelocityRevPerSecond := ReadVelocity.Velocity;
NeedsReference := ReadStatus.Valid AND NOT ReadStatus.Homed;
ReadyForTest := ParametersConfirmed AND ReadStatus.Valid AND ReadPosition.Valid
    AND ReadVelocity.Valid AND (ReadStatus.Homed OR EstablishTemporaryZero) AND NOT ReadStatus.ErrorStop
    AND (ReadStatus.Disabled OR ReadStatus.Standstill)
    AND (ABS(ActualVelocityRevPerSecond) <= 0.001) AND NOT CancelTest;
StartEdge := RunOnce AND NOT PreviousRun;
PreviousRun := RunOnce;

IF Acknowledge AND NOT RunOnce AND NOT Active THEN
    State := 0; Passed := FALSE; Failed := FALSE;
    OutcomeUnknown := FALSE; DiagnosticCode := 0;
END_IF;

IF StartEdge AND (State = 0) THEN
    Passed := FALSE; Failed := FALSE; SequenceCompleted := FALSE;
    IF EstablishTemporaryZero THEN PlannedStartRev := 0.0;
    ELSE PlannedStartRev := ActualPositionRev; END_IF;
    OutcomeUnknown := FALSE; DiagnosticCode := 0;
    IF CancelTest THEN DiagnosticCode := 7110;
    ELSIF NOT ParametersConfirmed THEN DiagnosticCode := 7101;
    ELSIF NOT ReadStatus.Valid OR NOT ReadPosition.Valid OR NOT ReadVelocity.Valid THEN DiagnosticCode := 7103;
    ELSIF NOT ReadStatus.Homed AND NOT EstablishTemporaryZero THEN DiagnosticCode := 7102;
    ELSIF ReadStatus.ErrorStop THEN DiagnosticCode := 7104;
    ELSIF NOT (ReadStatus.Disabled OR ReadStatus.Standstill)
        OR NOT (ABS(ActualVelocityRevPerSecond) <= 0.001) THEN DiagnosticCode := 7106;
    ELSIF NOT (DistanceRev > 0.0) OR NOT (DistanceRev <= 1.5)
        OR NOT (ReverseDistanceRev > 0.0) OR NOT (ReverseDistanceRev <= 1.0)
        OR NOT (VelocityRevPerSecond > 0.0) OR NOT (VelocityRevPerSecond <= 0.01)
        OR NOT (PositionToleranceRev > 0.0) OR NOT (PositionToleranceRev <= DistanceRev / 4.0)
        OR NOT (ActualPositionRev >= -2.0) OR NOT (ActualPositionRev <= 2.0)
        OR NOT (PlannedStartRev + DistanceRev >= -2.0) OR NOT (PlannedStartRev + DistanceRev <= 2.0)
        OR NOT (PlannedStartRev + DistanceRev - ReverseDistanceRev >= -2.0)
        OR NOT (PlannedStartRev + DistanceRev - ReverseDistanceRev <= 2.0) THEN DiagnosticCode := 7105;
    ELSE
        ForwardDistanceCommand := DistanceRev;
        ReverseDistanceCommand := ReverseDistanceRev;
        VelocityCommand := VelocityRevPerSecond;
        ToleranceCommand := PositionToleranceRev;
        HomeRequested := EstablishTemporaryZero;
        StartPositionRev := ActualPositionRev;
        OutwardPositionRev := StartPositionRev + ForwardDistanceCommand;
        ReturnPositionRev := OutwardPositionRev - ReverseDistanceCommand;
        Active := TRUE; State := 10;
    END_IF;
    IF DiagnosticCode <> 0 THEN Failed := TRUE; State := 900; END_IF;
END_IF;

TotalTimer(IN := Active, PT := T#300s);
DwellTimer(IN := State = 40, PT := T#2s);
StopTimer(IN := State = 80, PT := T#2s);
DisableTimer(IN := State = 90, PT := T#2s);

IF Active AND (State < 80) THEN
    IF CancelTest OR NOT ParametersConfirmed THEN DiagnosticCode := 7110; Failed := TRUE; State := 80;
    ELSIF TotalTimer.Q THEN DiagnosticCode := 7111; Failed := TRUE; State := 80;
    ELSIF NOT ReadStatus.Valid OR NOT ReadPosition.Valid OR NOT ReadVelocity.Valid
        OR ReadStatus.ErrorStop THEN DiagnosticCode := 7112; Failed := TRUE; State := 80;
    ELSIF NOT (ActualPositionRev >= -2.0) OR NOT (ActualPositionRev <= 2.0) THEN
        DiagnosticCode := 7113; Failed := TRUE; State := 80;
    END_IF;
END_IF;

(* Only reads execute before a verified start. There is no cold-start disable command. *)
IF Active THEN
    PowerDemand := State < 90;
    StopAxis(Axis := AXIS_IS620N, Execute := State = 80);
    Power(Axis := AXIS_IS620N, Enable := PowerDemand);
    Home(Axis := AXIS_IS620N, Execute := State = 20);
    MoveOut(Axis := AXIS_IS620N, Execute := State = 30,
        Position := OutwardPositionRev, Velocity := VelocityCommand);
    MoveBack(Axis := AXIS_IS620N, Execute := State = 50,
        Position := ReturnPositionRev, Velocity := VelocityCommand);

    IF (State < 80) AND (Power.Error OR Home.Error OR MoveOut.Error OR MoveBack.Error
        OR ((State = 20) AND Home.CommandAborted)
        OR ((State = 30) AND MoveOut.CommandAborted)
        OR ((State = 50) AND MoveBack.CommandAborted)) THEN
        DiagnosticCode := 7114; Failed := TRUE; State := 80;
    END_IF;

    CASE State OF
    10:
        IF Power.Status AND NOT Power.Busy AND ReadStatus.Standstill THEN
            IF HomeRequested THEN State := 20; ELSE State := 30; END_IF;
        END_IF;
    20:
        IF Home.Done AND ReadStatus.Valid AND ReadStatus.Homed AND ReadStatus.Standstill THEN
            StartPositionRev := ActualPositionRev;
            OutwardPositionRev := StartPositionRev + ForwardDistanceCommand;
            ReturnPositionRev := OutwardPositionRev - ReverseDistanceCommand;
            IF (OutwardPositionRev >= -2.0) AND (OutwardPositionRev <= 2.0)
                AND (ReturnPositionRev >= -2.0) AND (ReturnPositionRev <= 2.0) THEN State := 30;
            ELSE DiagnosticCode := 7113; Failed := TRUE; State := 80; END_IF;
        END_IF;
    30:
        IF MoveOut.Done AND ReadPosition.Valid
            AND (ABS(ActualPositionRev - OutwardPositionRev) <= ToleranceCommand) THEN State := 40; END_IF;
    40:
        IF DwellTimer.Q THEN State := 50; END_IF;
    50:
        IF MoveBack.Done AND ReadPosition.Valid
            AND (ABS(ActualPositionRev - ReturnPositionRev) <= ToleranceCommand) THEN
            SequenceCompleted := TRUE; State := 80;
        END_IF;
    80:
        IF StopAxis.Done AND ReadVelocity.Valid AND (ABS(ActualVelocityRevPerSecond) <= 0.001) THEN State := 90;
        ELSIF StopAxis.Error OR StopTimer.Q THEN
            DiagnosticCode := 7115; Failed := TRUE; OutcomeUnknown := TRUE; State := 90;
        END_IF;
    90:
        IF ReadStatus.Valid AND ReadStatus.Disabled AND NOT Power.Busy THEN
            Active := FALSE;
            IF SequenceCompleted AND NOT Failed AND NOT OutcomeUnknown THEN
                Passed := TRUE; CompletedTests := CompletedTests + 1; State := 100;
            ELSE Failed := TRUE; State := 900;
            END_IF;
        ELSIF Power.Error OR DisableTimer.Q THEN
            DiagnosticCode := 7116; Failed := TRUE; OutcomeUnknown := TRUE;
            Active := FALSE; State := 999;
        END_IF;
    END_CASE;
END_IF;
END_PROGRAM

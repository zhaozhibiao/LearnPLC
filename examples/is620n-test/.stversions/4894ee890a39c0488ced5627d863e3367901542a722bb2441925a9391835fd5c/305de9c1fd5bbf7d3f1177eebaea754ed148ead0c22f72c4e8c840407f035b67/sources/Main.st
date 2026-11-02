PROGRAM Main
VAR
    (* One rising edge requests one unloaded-motor round trip.
       These stay FALSE until the actual controller uses verified rev units.
       Current-position homing needs the commissioned method-35 controller profile.
       No alarm reset or switch-search homing is performed. *)
    RunOnce : BOOL := FALSE;
    ParametersConfirmed : BOOL := FALSE;
    EstablishTemporaryZero : BOOL := TRUE;
    CancelTest : BOOL := FALSE;
    Acknowledge : BOOL := FALSE;
    DistanceRev : LREAL := 0.01;
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
    CompletedRoundTrips : UINT;
    PreviousRun : BOOL;
    StartEdge : BOOL;
    PowerDemand : BOOL;
    Returned : BOOL;
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
    Passed := FALSE; Failed := FALSE; Returned := FALSE;
    OutcomeUnknown := FALSE; DiagnosticCode := 0;
    IF CancelTest THEN DiagnosticCode := 7110;
    ELSIF NOT ParametersConfirmed THEN DiagnosticCode := 7101;
    ELSIF NOT ReadStatus.Valid OR NOT ReadPosition.Valid OR NOT ReadVelocity.Valid THEN DiagnosticCode := 7103;
    ELSIF NOT ReadStatus.Homed AND NOT EstablishTemporaryZero THEN DiagnosticCode := 7102;
    ELSIF ReadStatus.ErrorStop THEN DiagnosticCode := 7104;
    ELSIF NOT (ReadStatus.Disabled OR ReadStatus.Standstill)
        OR NOT (ABS(ActualVelocityRevPerSecond) <= 0.001) THEN DiagnosticCode := 7106;
    ELSIF NOT (DistanceRev > 0.0) OR NOT (DistanceRev <= 0.01)
        OR NOT (VelocityRevPerSecond > 0.0) OR NOT (VelocityRevPerSecond <= 0.01)
        OR NOT (PositionToleranceRev > 0.0) OR NOT (PositionToleranceRev <= DistanceRev / 4.0)
        OR NOT (ABS(ActualPositionRev) + DistanceRev <= 1.0) THEN DiagnosticCode := 7105;
    ELSE
        StartPositionRev := ActualPositionRev;
        OutwardPositionRev := StartPositionRev + DistanceRev;
        Active := TRUE; State := 10;
    END_IF;
    IF DiagnosticCode <> 0 THEN Failed := TRUE; State := 900; END_IF;
END_IF;

TotalTimer(IN := Active, PT := T#10s);
DwellTimer(IN := (State = 40) OR (State = 60), PT := T#250ms);
StopTimer(IN := State = 80, PT := T#2s);
DisableTimer(IN := State = 90, PT := T#2s);

IF Active AND (State < 80) THEN
    IF CancelTest THEN DiagnosticCode := 7110; Failed := TRUE; State := 80;
    ELSIF TotalTimer.Q THEN DiagnosticCode := 7111; Failed := TRUE; State := 80;
    ELSIF NOT ReadStatus.Valid OR NOT ReadPosition.Valid OR NOT ReadVelocity.Valid
        OR ReadStatus.ErrorStop THEN DiagnosticCode := 7112; Failed := TRUE; State := 80;
    ELSIF (State >= 30) AND (ABS(ActualPositionRev - StartPositionRev) > DistanceRev * 1.5) THEN
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
        Position := OutwardPositionRev, Velocity := VelocityRevPerSecond);
    MoveBack(Axis := AXIS_IS620N, Execute := State = 50,
        Position := StartPositionRev, Velocity := VelocityRevPerSecond);

    IF (State < 80) AND (Power.Error OR Home.Error OR MoveOut.Error OR MoveBack.Error
        OR ((State = 20) AND Home.CommandAborted)
        OR ((State = 30) AND MoveOut.CommandAborted)
        OR ((State = 50) AND MoveBack.CommandAborted)) THEN
        DiagnosticCode := 7114; Failed := TRUE; State := 80;
    END_IF;

    CASE State OF
    10:
        IF Power.Status AND NOT Power.Busy AND ReadStatus.Standstill THEN
            IF EstablishTemporaryZero THEN State := 20; ELSE State := 30; END_IF;
        END_IF;
    20:
        IF Home.Done AND ReadStatus.Valid AND ReadStatus.Homed AND ReadStatus.Standstill THEN
            StartPositionRev := ActualPositionRev;
            OutwardPositionRev := StartPositionRev + DistanceRev;
            State := 30;
        END_IF;
    30:
        IF MoveOut.Done AND ReadPosition.Valid
            AND (ABS(ActualPositionRev - OutwardPositionRev) <= PositionToleranceRev) THEN State := 40; END_IF;
    40:
        IF DwellTimer.Q THEN State := 50; END_IF;
    50:
        IF MoveBack.Done AND ReadPosition.Valid
            AND (ABS(ActualPositionRev - StartPositionRev) <= PositionToleranceRev) THEN
            Returned := TRUE; State := 60;
        END_IF;
    60:
        IF DwellTimer.Q THEN State := 80; END_IF;
    80:
        IF StopAxis.Done AND ReadVelocity.Valid AND (ABS(ActualVelocityRevPerSecond) <= 0.001) THEN State := 90;
        ELSIF StopAxis.Error OR StopTimer.Q THEN
            DiagnosticCode := 7115; Failed := TRUE; OutcomeUnknown := TRUE; State := 90;
        END_IF;
    90:
        IF ReadStatus.Valid AND ReadStatus.Disabled AND NOT Power.Busy THEN
            Active := FALSE;
            IF Returned AND NOT Failed AND NOT OutcomeUnknown THEN
                Passed := TRUE; CompletedRoundTrips := CompletedRoundTrips + 1; State := 100;
            ELSE Failed := TRUE; State := 900;
            END_IF;
        ELSIF Power.Error OR DisableTimer.Q THEN
            DiagnosticCode := 7116; Failed := TRUE; OutcomeUnknown := TRUE;
            Active := FALSE; State := 999;
        END_IF;
    END_CASE;
END_IF;
END_PROGRAM

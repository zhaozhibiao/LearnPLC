TYPE AXIS_REF : UINT; END_TYPE
VAR_GLOBAL
    AB_ScanCounter : ULINT;
    AB_Request : ARRAY[1..64] OF ULINT;
    AB_RequestSequence : ARRAY[1..64] OF ULINT;
    AB_Kind : ARRAY[1..64] OF ULINT;
    AB_Position : ARRAY[1..64] OF LREAL;
    AB_Velocity : ARRAY[1..64] OF LREAL;
    AB_Acceleration : ARRAY[1..64] OF LREAL;
    AB_Deceleration : ARRAY[1..64] OF LREAL;
    AB_Jerk : ARRAY[1..64] OF LREAL;
    AB_StopHeld : ARRAY[1..64] OF BOOL;
    AB_FeedbackRequest : ARRAY[1..64] OF ULINT;
    AB_RejectedRequest : ARRAY[1..64] OF ULINT;
    AB_RejectedError : ARRAY[1..64] OF UDINT;
    AB_Busy : ARRAY[1..64] OF BOOL;
    AB_Done : ARRAY[1..64] OF BOOL;
    AB_Error : ARRAY[1..64] OF UDINT;
    AB_Aborted : ARRAY[1..64] OF BOOL;
    AB_Enabled : ARRAY[1..64] OF BOOL;
    AB_Homed : ARRAY[1..64] OF BOOL;
    AB_State : ARRAY[1..64] OF ULINT;
    AB_MotionId : ARRAY[1..64] OF ULINT;
    AB_ActualPosition : ARRAY[1..64] OF LREAL;
    AB_ActualVelocity : ARRAY[1..64] OF LREAL;
END_VAR

(* Internal bounded arbiter. No axis index is accessed before validation. *)
FUNCTION_BLOCK AB_Command
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; Kind : ULINT; Position : LREAL; Velocity : LREAL; Acceleration : LREAL := 0.0; Deceleration : LREAL := 0.0; Jerk : LREAL := 0.0; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; AcceptedAxis : AXIS_REF; AcceptedVelocity : LREAL; END_VAR
VAR PreviousExecute : BOOL; LocalError : UDINT; OwnedAxis : AXIS_REF; OwnedKind : ULINT; PriorStopHeld : BOOL; Running : BOOL; Terminal : BOOL; Rising : BOOL; END_VAR
Rising := Execute AND NOT PreviousExecute;
PreviousExecute := Execute;
(* Reset only a previously observed terminal result. Early Execute release does
   not cancel execution and the eventual result remains visible for one call. *)
IF Terminal AND NOT Execute THEN
    Terminal := FALSE; LocalError := 0; Busy := FALSE; Active := FALSE;
    Done := FALSE; CommandAborted := FALSE; ErrorID := 0;
END_IF;
IF Rising AND (NOT Running OR (Kind = 2)) THEN
    LocalError := 0; Terminal := FALSE; ErrorID := 0;
    Busy := FALSE; Active := FALSE; Done := FALSE; CommandAborted := FALSE;
    IF (Axis < 1) OR (Axis > 64) THEN
        LocalError := 2001;
    ELSIF (Position <> Position) OR (Velocity <> Velocity) OR (ABS(Position) > 1.0E300) OR (ABS(Velocity) > 1.0E300) OR ((Kind = 5) AND (Velocity <= 0.0)) THEN
        LocalError := 2007;
    ELSIF (Acceleration <> Acceleration) OR (Deceleration <> Deceleration) OR (Jerk <> Jerk) OR (Acceleration < 0.0) OR (Deceleration < 0.0) OR (Jerk < 0.0) OR (Acceleration > 1.7976931348623157E308) OR (Deceleration > 1.7976931348623157E308) OR (Jerk > 1.7976931348623157E308) THEN
        LocalError := 2007;
    ELSIF (Kind <> 2) AND (Kind <> 7) AND (AB_StopHeld[Axis] OR ((AB_Request[Axis] <> 0) AND (((AB_FeedbackRequest[Axis] <> AB_Request[Axis]) AND (AB_RejectedRequest[Axis] <> AB_Request[Axis])) OR AB_Busy[Axis]))) THEN
        LocalError := 2006;
    ELSIF (AB_RequestSequence[Axis] = ULINT#18446744073709551615) OR (AB_Request[Axis] = ULINT#18446744073709551615) THEN
        LocalError := 2009;
    ELSE
        IF AB_Request[Axis] > AB_RequestSequence[Axis] THEN AB_RequestSequence[Axis] := AB_Request[Axis]; END_IF;
        AB_RequestSequence[Axis] := AB_RequestSequence[Axis] + 1;
        AB_Request[Axis] := AB_RequestSequence[Axis];
        RequestId := AB_Request[Axis];
        OwnedAxis := Axis; OwnedKind := Kind; PriorStopHeld := AB_StopHeld[Axis]; AcceptedAxis := Axis; AcceptedVelocity := Velocity;
        AB_Kind[Axis] := Kind;
        AB_Position[Axis] := Position;
        AB_Velocity[Axis] := Velocity;
        AB_Acceleration[Axis] := Acceleration;
        AB_Deceleration[Axis] := Deceleration;
        AB_Jerk[Axis] := Jerk;
        IF Kind = 7 THEN AB_StopHeld[Axis] := TRUE; ELSIF Kind = 2 THEN AB_StopHeld[Axis] := FALSE; END_IF;
        Running := TRUE; Busy := TRUE; Active := FALSE;
    END_IF;
END_IF;
IF (Kind = 7) AND NOT Execute AND (RequestId <> 0) AND (OwnedAxis >= 1) AND (OwnedAxis <= 64) THEN
    IF AB_FeedbackRequest[OwnedAxis] = RequestId THEN
        AB_StopHeld[OwnedAxis] := FALSE;
        (* A rejected newer image cannot release the retained accepted Stop.
           Re-send that accepted owner's duplicate; sequence remains monotonic. *)
        IF AB_Request[OwnedAxis] = AB_RejectedRequest[OwnedAxis] THEN
            AB_Request[OwnedAxis] := RequestId; AB_Kind[OwnedAxis] := 7;
        END_IF;
    ELSIF (AB_Request[OwnedAxis] = RequestId) AND (AB_RejectedRequest[OwnedAxis] <> RequestId) THEN
        AB_StopHeld[OwnedAxis] := FALSE;
    END_IF;
END_IF;
IF LocalError <> 0 THEN
    ErrorID := LocalError; Running := FALSE; Terminal := TRUE; Busy := FALSE; Active := FALSE; Done := FALSE;
ELSIF Running THEN
    IF AB_RejectedRequest[OwnedAxis] = RequestId THEN
        Running := FALSE; Terminal := TRUE; Busy := FALSE; Active := FALSE; Done := FALSE; CommandAborted := FALSE;
        ErrorID := AB_RejectedError[OwnedAxis]; IF ErrorID = 0 THEN ErrorID := 2007; END_IF;
        IF AB_Request[OwnedAxis] = RequestId THEN AB_StopHeld[OwnedAxis] := PriorStopHeld; END_IF;
    ELSIF (AB_FeedbackRequest[OwnedAxis] > RequestId) OR ((AB_FeedbackRequest[OwnedAxis] < RequestId) AND (AB_RejectedRequest[OwnedAxis] > RequestId)) THEN
        Running := FALSE; Terminal := TRUE; Busy := FALSE; Active := FALSE; Done := FALSE; CommandAborted := TRUE; ErrorID := 0;
    ELSIF AB_FeedbackRequest[OwnedAxis] = RequestId THEN
        Active := AB_Busy[OwnedAxis];
        ErrorID := AB_Error[OwnedAxis];
        IF ErrorID <> 0 THEN
            Running := FALSE; Terminal := TRUE; Busy := FALSE; Active := FALSE; Done := FALSE; CommandAborted := FALSE;
        ELSIF AB_Aborted[OwnedAxis] THEN
            Running := FALSE; Terminal := TRUE; Busy := FALSE; Active := FALSE; Done := FALSE; CommandAborted := TRUE;
        ELSIF AB_Done[OwnedAxis] THEN
            Running := FALSE; Terminal := TRUE; Busy := FALSE; Active := FALSE; Done := TRUE;
            (* Completion is actual terminal feedback, never merely request acknowledgement. *)
            IF ((OwnedKind = 1) AND NOT AB_Enabled[OwnedAxis]) OR
               ((OwnedKind = 2) AND (AB_Enabled[OwnedAxis] OR (AB_State[OwnedAxis] <> 1))) OR
               ((OwnedKind = 3) AND ((AB_State[OwnedAxis] = 0) OR (AB_State[OwnedAxis] = 7))) OR
               ((OwnedKind = 4) AND (NOT AB_Homed[OwnedAxis] OR (AB_State[OwnedAxis] <> 2))) OR
               ((OwnedKind = 5) AND ((AB_MotionId[OwnedAxis] = 0) OR (AB_State[OwnedAxis] <> 2))) OR
               (OwnedKind = 6) OR
               ((OwnedKind = 7) AND (AB_State[OwnedAxis] <> 1) AND (AB_State[OwnedAxis] <> 2)) THEN
                Done := FALSE; Busy := FALSE; Active := FALSE; ErrorID := 2008;
            END_IF;
        END_IF;
    END_IF;
END_IF;
(* Stop remains an exclusive owner until Execute is released, even at zero speed. *)
IF (Kind = 7) AND Done AND Execute THEN Busy := TRUE; Active := TRUE; END_IF;
Error := ErrorID <> 0;
END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_Reset
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; END_VAR
VAR Command : AB_Command; END_VAR
Command(Axis := Axis, Execute := Execute, Kind := 3, Position := 0.0, Velocity := 0.0);
Busy := Command.Busy; Done := Command.Done; Active := Command.Active; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;

END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_Home
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; END_VAR
VAR Command : AB_Command; END_VAR
Command(Axis := Axis, Execute := Execute, Kind := 4, Position := 0.0, Velocity := 0.0);
Busy := Command.Busy; Done := Command.Done; Active := Command.Active; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;

END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_MoveAbsolute
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; Position : LREAL; Velocity : LREAL; Acceleration : LREAL := 0.0; Deceleration : LREAL := 0.0; Jerk : LREAL := 0.0; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; END_VAR
VAR Command : AB_Command; END_VAR
Command(Axis := Axis, Execute := Execute, Kind := 5, Position := Position, Velocity := Velocity, Acceleration := Acceleration, Deceleration := Deceleration, Jerk := Jerk);
Busy := Command.Busy; Done := Command.Done; Active := Command.Active; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;

END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_MoveVelocity
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; Velocity : LREAL; Acceleration : LREAL := 0.0; Deceleration : LREAL := 0.0; Jerk : LREAL := 0.0; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; InVelocity : BOOL; END_VAR
VAR Command : AB_Command; END_VAR
Command(Axis := Axis, Execute := Execute, Kind := 6, Position := 0.0, Velocity := Velocity, Acceleration := Acceleration, Deceleration := Deceleration, Jerk := Jerk);
Busy := Command.Busy; Done := Command.Done; Active := Command.Active; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;
InVelocity := FALSE; IF (Command.AcceptedAxis >= 1) AND (Command.AcceptedAxis <= 64) THEN InVelocity := Active AND NOT Error AND NOT CommandAborted AND (AB_State[Command.AcceptedAxis] <> 0) AND (AB_State[Command.AcceptedAxis] <> 7) AND (AB_FeedbackRequest[Command.AcceptedAxis] = RequestId) AND (ABS(AB_ActualVelocity[Command.AcceptedAxis] - Command.AcceptedVelocity) <= 0.000001); END_IF;
END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_Stop
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; Deceleration : LREAL := 0.0; Jerk : LREAL := 0.0; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; END_VAR
VAR Command : AB_Command; END_VAR
Command(Axis := Axis, Execute := Execute, Kind := 7, Position := 0.0, Velocity := 0.0, Acceleration := 0.0, Deceleration := Deceleration, Jerk := Jerk);
Busy := Command.Busy; Done := Command.Done; Active := Command.Active; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;

END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_Power
VAR_INPUT Axis : AXIS_REF; Enable : BOOL; END_VAR
VAR_OUTPUT Status : BOOL; Busy : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; END_VAR
VAR Command : AB_Command; PreviousEnable : BOOL; Initialized : BOOL; Pulse : BOOL; PendingEnable : BOOL; Kind : ULINT; END_VAR
Pulse := NOT Initialized;
IF Enable <> PreviousEnable THEN
    IF NOT Enable THEN Pulse := TRUE; PendingEnable := FALSE;
    ELSIF Command.Busy THEN PendingEnable := TRUE;
    ELSE Pulse := TRUE;
    END_IF;
END_IF;
IF NOT Enable THEN PendingEnable := FALSE; END_IF;
(* A new Enable edge during Disable waits for its successful acknowledgement.
   Failure cancels that pending edge: reconnect/fault recovery never retries it. *)
IF PendingEnable AND NOT Command.Busy THEN
    PendingEnable := FALSE;
    IF Enable AND Command.Done AND NOT Command.Error THEN
        IF (Axis >= 1) AND (Axis <= 64) THEN
            IF AB_State[Axis] = 1 THEN Pulse := TRUE; END_IF;
        END_IF;
    END_IF;
END_IF;
Initialized := TRUE;
(* Hold this level-controlled operation's result between Enable changes. *)
IF Pulse THEN
    IF Enable THEN Kind := 1; ELSE Kind := 2; END_IF;
    Command(Axis := Axis, Execute := FALSE, Kind := Kind, Position := 0.0, Velocity := 0.0);
END_IF;
Command(Axis := Axis, Execute := TRUE, Kind := Kind, Position := 0.0, Velocity := 0.0);
PreviousEnable := Enable;
Status := FALSE;
IF (Axis >= 1) AND (Axis <= 64) THEN Status := (AB_State[Axis] <> 0) AND (AB_State[Axis] <> 7) AND AB_Enabled[Axis]; END_IF;
Busy := Command.Busy; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;
IF (Axis >= 1) AND (Axis <= 64) THEN
    IF (AB_State[Axis] = 0) AND (AB_Error[Axis] <> 0) THEN
        Busy := FALSE; Error := TRUE; ErrorID := AB_Error[Axis];
    ELSIF AB_State[Axis] = 7 THEN
        Busy := FALSE; Error := TRUE; ErrorID := 2005;
    END_IF;
END_IF;
END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_ReadAxisStatus
VAR_INPUT Axis : AXIS_REF; Enable : BOOL; END_VAR
VAR_OUTPUT Valid : BOOL; Busy : BOOL; Error : BOOL; ErrorID : UDINT; Disabled : BOOL; Standstill : BOOL; Homing : BOOL; DiscreteMotion : BOOL; ContinuousMotion : BOOL; SynchronizedMotion : BOOL; Stopping : BOOL; ErrorStop : BOOL; Homed : BOOL; END_VAR
Valid := FALSE; Busy := FALSE; ErrorID := 0;
Disabled := FALSE; Standstill := FALSE; Homing := FALSE; DiscreteMotion := FALSE; ContinuousMotion := FALSE; Stopping := FALSE; ErrorStop := FALSE; Homed := FALSE;
SynchronizedMotion := FALSE;
IF Enable THEN
  IF (Axis < 1) OR (Axis > 64) THEN ErrorID := 2001;
  ELSIF AB_State[Axis] = 0 THEN
    ErrorID := AB_Error[Axis]; IF ErrorID = 0 THEN ErrorID := 2004; END_IF;
  ELSE
    Valid := TRUE; Busy := TRUE;
    Disabled := AB_State[Axis] = 1; Standstill := AB_State[Axis] = 2; Homing := AB_State[Axis] = 3;
    DiscreteMotion := AB_State[Axis] = 4; ContinuousMotion := AB_State[Axis] = 5; Stopping := AB_State[Axis] = 6; ErrorStop := AB_State[Axis] = 7; Homed := AB_Homed[Axis];
    IF AB_StopHeld[Axis] AND Standstill THEN Standstill := FALSE; Stopping := TRUE; END_IF;
  END_IF;
END_IF;
Error := ErrorID <> 0;
END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_ReadActualPosition
VAR_INPUT Axis : AXIS_REF; Enable : BOOL; END_VAR
VAR_OUTPUT Valid : BOOL; Busy : BOOL; Error : BOOL; ErrorID : UDINT; Position : LREAL; END_VAR
Valid := FALSE; Busy := FALSE; ErrorID := 0;
IF Enable THEN
  IF (Axis < 1) OR (Axis > 64) THEN ErrorID := 2001;
  ELSIF AB_State[Axis] = 0 THEN ErrorID := AB_Error[Axis]; IF ErrorID = 0 THEN ErrorID := 2004; END_IF;
  ELSE Valid := TRUE; Busy := TRUE; Position := AB_ActualPosition[Axis]; END_IF;
END_IF;
Error := ErrorID <> 0;
END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_ReadActualVelocity
VAR_INPUT Axis : AXIS_REF; Enable : BOOL; END_VAR
VAR_OUTPUT Valid : BOOL; Busy : BOOL; Error : BOOL; ErrorID : UDINT; Velocity : LREAL; END_VAR
Valid := FALSE; Busy := FALSE; ErrorID := 0;
IF Enable THEN
  IF (Axis < 1) OR (Axis > 64) THEN ErrorID := 2001;
  ELSIF AB_State[Axis] = 0 THEN ErrorID := AB_Error[Axis]; IF ErrorID = 0 THEN ErrorID := 2004; END_IF;
  ELSE Valid := TRUE; Busy := TRUE; Velocity := AB_ActualVelocity[Axis]; END_IF;
END_IF;
Error := ErrorID <> 0;
END_FUNCTION_BLOCK

(* Canonical public name; MC_ReadAxisStatus remains source-compatible. Homed is an extension. *)
FUNCTION_BLOCK MC_ReadStatus
VAR_INPUT Axis : AXIS_REF; Enable : BOOL; END_VAR
VAR_OUTPUT Valid : BOOL; Busy : BOOL; Error : BOOL; ErrorID : UDINT; Disabled : BOOL; Standstill : BOOL; Homing : BOOL; DiscreteMotion : BOOL; ContinuousMotion : BOOL; SynchronizedMotion : BOOL; Stopping : BOOL; ErrorStop : BOOL; Homed : BOOL; END_VAR
VAR Reader : MC_ReadAxisStatus; END_VAR
Reader(Axis := Axis, Enable := Enable);
Valid := Reader.Valid; Busy := Reader.Busy; Error := Reader.Error; ErrorID := Reader.ErrorID;
Disabled := Reader.Disabled; Standstill := Reader.Standstill; Homing := Reader.Homing;
DiscreteMotion := Reader.DiscreteMotion; ContinuousMotion := Reader.ContinuousMotion;
SynchronizedMotion := Reader.SynchronizedMotion; Stopping := Reader.Stopping; ErrorStop := Reader.ErrorStop; Homed := Reader.Homed;
END_FUNCTION_BLOCK







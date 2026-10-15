TYPE AXIS_REF : UINT; END_TYPE
VAR_GLOBAL
    AB_ScanCounter : ULINT;
    AB_Request : ARRAY[1..64] OF ULINT;
    AB_Kind : ARRAY[1..64] OF ULINT;
    AB_Position : ARRAY[1..64] OF LREAL;
    AB_Velocity : ARRAY[1..64] OF LREAL;
    AB_StopHeld : ARRAY[1..64] OF BOOL;
    AB_FeedbackRequest : ARRAY[1..64] OF ULINT;
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
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; Kind : ULINT; Position : LREAL; Velocity : LREAL; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; AcceptedAxis : AXIS_REF; AcceptedVelocity : LREAL; END_VAR
VAR PreviousExecute : BOOL; LocalError : UDINT; OwnedAxis : AXIS_REF; END_VAR
IF (Axis < 1) OR (Axis > 64) THEN
    LocalError := 2001;
ELSIF Execute AND NOT PreviousExecute THEN
    LocalError := 0;
    IF (Position <> Position) OR (Velocity <> Velocity) OR (ABS(Position) > 1.0E300) OR (ABS(Velocity) > 1.0E300) OR ((Kind = 5) AND (Velocity <= 0.0)) THEN
        LocalError := 2007;
    ELSIF (Kind <> 2) AND (Kind <> 7) AND (AB_StopHeld[Axis] OR ((AB_Request[Axis] <> 0) AND ((AB_FeedbackRequest[Axis] <> AB_Request[Axis]) OR AB_Busy[Axis]))) THEN
        LocalError := 2006;
    ELSIF AB_Request[Axis] = ULINT#18446744073709551615 THEN
        LocalError := 2009;
    ELSE
        AB_Request[Axis] := AB_Request[Axis] + 1;
        RequestId := AB_Request[Axis];
        OwnedAxis := Axis; AcceptedAxis := Axis; AcceptedVelocity := Velocity;
        AB_Kind[Axis] := Kind;
        AB_Position[Axis] := Position;
        AB_Velocity[Axis] := Velocity;
        IF Kind = 7 THEN AB_StopHeld[Axis] := TRUE; ELSIF Kind = 2 THEN AB_StopHeld[Axis] := FALSE; END_IF;
        Busy := TRUE; Active := TRUE; Done := FALSE; CommandAborted := FALSE;
    END_IF;
END_IF;
PreviousExecute := Execute;
IF (Kind = 7) AND NOT Execute AND (RequestId <> 0) AND (OwnedAxis >= 1) AND (OwnedAxis <= 64) THEN
    IF AB_Request[OwnedAxis] = RequestId THEN AB_StopHeld[OwnedAxis] := FALSE; END_IF;
END_IF;
IF LocalError <> 0 THEN
    ErrorID := LocalError; Busy := FALSE; Active := FALSE; Done := FALSE;
ELSIF RequestId <> 0 THEN
    IF (AB_Request[OwnedAxis] <> RequestId) OR (AB_FeedbackRequest[OwnedAxis] > RequestId) THEN
        Busy := FALSE; Active := FALSE; Done := FALSE; CommandAborted := TRUE; ErrorID := 0;
    ELSIF AB_FeedbackRequest[OwnedAxis] = RequestId THEN
        Busy := AB_Busy[OwnedAxis]; Active := Busy; Done := AB_Done[OwnedAxis];
                CommandAborted := AB_Aborted[OwnedAxis]; ErrorID := AB_Error[OwnedAxis];
        IF Done THEN
            (* Completion is actual terminal feedback, never merely request acknowledgement. *)
            IF ((AB_Kind[OwnedAxis] = 1) AND NOT AB_Enabled[OwnedAxis]) OR
               ((AB_Kind[OwnedAxis] = 2) AND (AB_Enabled[OwnedAxis] OR (AB_State[OwnedAxis] <> 1))) OR
               ((AB_Kind[OwnedAxis] = 3) AND ((AB_State[OwnedAxis] = 0) OR (AB_State[OwnedAxis] = 7))) OR
               ((AB_Kind[OwnedAxis] = 4) AND (NOT AB_Homed[OwnedAxis] OR (AB_State[OwnedAxis] <> 2))) OR
               ((AB_Kind[OwnedAxis] = 5) AND ((AB_MotionId[OwnedAxis] = 0) OR (AB_State[OwnedAxis] <> 2))) OR
               (AB_Kind[OwnedAxis] = 6) OR
               ((AB_Kind[OwnedAxis] = 7) AND (((AB_State[OwnedAxis] <> 1) AND (AB_State[OwnedAxis] <> 2)) OR (ABS(AB_ActualVelocity[OwnedAxis]) > 0.000001))) THEN
                Done := FALSE; Busy := FALSE; Active := FALSE; ErrorID := 2008;
            END_IF;
        END_IF;
    END_IF;
END_IF;
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
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; Position : LREAL; Velocity : LREAL; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; END_VAR
VAR Command : AB_Command; END_VAR
Command(Axis := Axis, Execute := Execute, Kind := 5, Position := Position, Velocity := Velocity);
Busy := Command.Busy; Done := Command.Done; Active := Command.Active; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;

END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_MoveVelocity
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; Velocity : LREAL; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; InVelocity : BOOL; END_VAR
VAR Command : AB_Command; END_VAR
Command(Axis := Axis, Execute := Execute, Kind := 6, Position := 0.0, Velocity := Velocity);
Busy := Command.Busy; Done := Command.Done; Active := Command.Active; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;
InVelocity := FALSE; IF (Command.AcceptedAxis >= 1) AND (Command.AcceptedAxis <= 64) THEN InVelocity := Active AND (ABS(AB_ActualVelocity[Command.AcceptedAxis] - Command.AcceptedVelocity) <= 0.000001); END_IF;
END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_Stop
VAR_INPUT Axis : AXIS_REF; Execute : BOOL; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Active : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; END_VAR
VAR Command : AB_Command; END_VAR
Command(Axis := Axis, Execute := Execute, Kind := 7, Position := 0.0, Velocity := 0.0);
Busy := Command.Busy; Done := Command.Done; Active := Command.Active; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;

END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_Power
VAR_INPUT Axis : AXIS_REF; Enable : BOOL; END_VAR
VAR_OUTPUT Status : BOOL; Busy : BOOL; CommandAborted : BOOL; Error : BOOL; ErrorID : UDINT; RequestId : ULINT; END_VAR
VAR Command : AB_Command; PreviousEnable : BOOL; Initialized : BOOL; Pulse : BOOL; Kind : ULINT; END_VAR
Pulse := NOT Initialized OR (Enable <> PreviousEnable); Initialized := TRUE;
IF Enable THEN Kind := 1; ELSE Kind := 2; END_IF;
(* Reset edge memory every call, so consecutive state changes both submit. *)
Command(Axis := Axis, Execute := FALSE, Kind := Kind, Position := 0.0, Velocity := 0.0);
Command(Axis := Axis, Execute := Pulse, Kind := Kind, Position := 0.0, Velocity := 0.0);
PreviousEnable := Enable;
Status := FALSE;
IF (Axis >= 1) AND (Axis <= 64) THEN Status := AB_Enabled[Axis]; END_IF;
Busy := Command.Busy; CommandAborted := Command.CommandAborted; Error := Command.Error; ErrorID := Command.ErrorID; RequestId := Command.RequestId;
END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_ReadAxisStatus
VAR_INPUT Axis : AXIS_REF; Enable : BOOL; END_VAR
VAR_OUTPUT Valid : BOOL; Error : BOOL; ErrorID : UDINT; Disabled : BOOL; Standstill : BOOL; Homing : BOOL; DiscreteMotion : BOOL; ContinuousMotion : BOOL; Stopping : BOOL; ErrorStop : BOOL; Homed : BOOL; END_VAR
Valid := FALSE; ErrorID := 0;
Disabled := FALSE; Standstill := FALSE; Homing := FALSE; DiscreteMotion := FALSE; ContinuousMotion := FALSE; Stopping := FALSE; ErrorStop := FALSE; Homed := FALSE;
IF (Axis < 1) OR (Axis > 64) THEN ErrorID := 2001;
ELSIF Enable THEN
    ErrorID := AB_Error[Axis]; Valid := (AB_State[Axis] <> 0) AND (ErrorID = 0);
    Disabled := AB_State[Axis] = 1; Standstill := AB_State[Axis] = 2; Homing := AB_State[Axis] = 3;
    DiscreteMotion := AB_State[Axis] = 4; ContinuousMotion := AB_State[Axis] = 5; Stopping := AB_State[Axis] = 6; ErrorStop := AB_State[Axis] = 7; Homed := AB_Homed[Axis];
END_IF;
Error := ErrorID <> 0;
END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_ReadActualPosition
VAR_INPUT Axis : AXIS_REF; Enable : BOOL; END_VAR
VAR_OUTPUT Valid : BOOL; Error : BOOL; ErrorID : UDINT; Position : LREAL; END_VAR
Valid := FALSE; ErrorID := 0;
IF (Axis < 1) OR (Axis > 64) THEN ErrorID := 2001;
ELSIF Enable THEN ErrorID := AB_Error[Axis]; Valid := (AB_State[Axis] <> 0) AND (ErrorID = 0); Position := AB_ActualPosition[Axis]; END_IF;
Error := ErrorID <> 0;
END_FUNCTION_BLOCK

FUNCTION_BLOCK MC_ReadActualVelocity
VAR_INPUT Axis : AXIS_REF; Enable : BOOL; END_VAR
VAR_OUTPUT Valid : BOOL; Error : BOOL; ErrorID : UDINT; Velocity : LREAL; END_VAR
Valid := FALSE; ErrorID := 0;
IF (Axis < 1) OR (Axis > 64) THEN ErrorID := 2001;
ELSIF Enable THEN ErrorID := AB_Error[Axis]; Valid := (AB_State[Axis] <> 0) AND (ErrorID = 0); Velocity := AB_ActualVelocity[Axis]; END_IF;
Error := ErrorID <> 0;
END_FUNCTION_BLOCK







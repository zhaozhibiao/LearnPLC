(* One bounded command/feedback image for the configured PLC axis group.
   These are compiler-owned typed globals, copied by the after-scan native shim.
   MB_ScanCounter advances only in an actual PLC scan, never in the IPC thread. *)
VAR_GLOBAL
    MB_ScanCounter : ULINT;
    MB_RequestId : ULINT;
    MB_PathId : ULINT;
    MB_Version : ULINT;
    MB_Hash : STRING;
    MB_Stop : BOOL;
    MB_FeedbackId : ULINT;
    MB_Busy : BOOL;
    MB_Done : BOOL;
    MB_ErrorID : UDINT;
    MB_Progress : LREAL;
END_VAR

FUNCTION_BLOCK MC_ExecuteContinuousPath
VAR_INPUT
    Execute : BOOL;
    Stop : BOOL;
    PathId : ULINT;
    Version : ULINT;
    ContentHash : STRING;
END_VAR
VAR_OUTPUT
    Busy : BOOL;
    Done : BOOL;
    Error : BOOL;
    ErrorID : UDINT;
    Progress : LREAL;
    RequestId : ULINT;
END_VAR
VAR
    PreviousExecute : BOOL;
    LocalError : UDINT;
END_VAR
(* Exactly one configured call per scan; shim owns actual scan counter. *)
IF Execute AND NOT PreviousExecute THEN
    IF MB_Busy OR (MB_RequestId <> MB_FeedbackId) THEN
        LocalError := 1008;
    ELSE
        MB_RequestId := MB_RequestId + 1;
        MB_PathId := PathId;
        MB_Version := Version;
        MB_Hash := ContentHash;
        MB_Stop := FALSE;
        RequestId := MB_RequestId;
        LocalError := 0;
        Done := FALSE;
        Busy := TRUE;
        Progress := 0;
    END_IF;
END_IF;
PreviousExecute := Execute;
(* Only the owning instance can stop its request. The latch survives other FB
   calls and a one-scan Stop pulse; a newly accepted request clears it. *)
IF Stop AND (RequestId <> 0) AND (RequestId = MB_RequestId) THEN
    MB_Stop := TRUE;
END_IF;
IF (RequestId <> 0) AND (MB_FeedbackId = RequestId) THEN
    Busy := MB_Busy;
    Done := MB_Done;
    Progress := MB_Progress;
    ErrorID := MB_ErrorID;
ELSE
    ErrorID := LocalError;
END_IF;
IF LocalError <> 0 THEN
    ErrorID := LocalError;
END_IF;
Error := ErrorID <> 0;
END_FUNCTION_BLOCK



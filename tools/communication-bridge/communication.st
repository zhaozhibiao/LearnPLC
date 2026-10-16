(* SPDX-License-Identifier: MIT; Copyright (c) 2026 ST Workbench contributors *)
TYPE COMM_REF : UINT; END_TYPE
VAR_GLOBAL
 CB_ScanCounter : ULINT;
 CB_Request : ARRAY[1..16] OF ULINT;
 CB_Action : ARRAY[1..16] OF ULINT;
 CB_Function : ARRAY[1..16] OF ULINT;
 CB_Unit : ARRAY[1..16] OF ULINT;
 CB_Address : ARRAY[1..16] OF ULINT;
 CB_Quantity : ARRAY[1..16] OF ULINT;
 CB_ReadAddress : ARRAY[1..16] OF ULINT;
 CB_ReadQuantity : ARRAY[1..16] OF ULINT;
 CB_Length : ARRAY[1..16] OF ULINT;
 CB_Data : ARRAY[0..8191] OF BYTE;
 CB_Feedback : ARRAY[1..16] OF ULINT;
 CB_Busy : ARRAY[1..16] OF BOOL;
 CB_Done : ARRAY[1..16] OF BOOL;
 CB_Error : ARRAY[1..16] OF UDINT;
 CB_Unknown : ARRAY[1..16] OF BOOL;
 CB_Acknowledged : ARRAY[1..16] OF BOOL;
 CB_ReceivedLength : ARRAY[1..16] OF ULINT;
 CB_ReceivedData : ARRAY[0..8191] OF BYTE;
END_VAR

FUNCTION_BLOCK CB_Command
VAR_INPUT
 Channel : COMM_REF; Execute : BOOL; Action : UINT; FunctionCode : UINT; UnitId : UINT := 1;
 Address : UINT; Quantity : UINT := 1; ReadAddress : UINT; ReadQuantity : UINT;
 DataLength : UINT; Data : ARRAY[0..511] OF BYTE;
END_VAR
VAR_OUTPUT
 Busy : BOOL; Done : BOOL; Error : BOOL; ErrorID : UDINT; Unknown : BOOL;
 Acknowledged : BOOL; RequestId : ULINT; ReceivedLength : UINT; ReceivedData : ARRAY[0..511] OF BYTE;
END_VAR
VAR Previous : BOOL; OwnedChannel : UINT; LocalError : UDINT; I : DINT; Base : DINT; END_VAR
IF (Channel < 1) OR (Channel > 16) THEN
 LocalError := 6001; Unknown := FALSE; Acknowledged := FALSE; ReceivedLength := 0;
ELSIF Execute AND NOT Previous THEN
 LocalError := 0; Done := FALSE; Unknown := FALSE; Acknowledged := FALSE; ReceivedLength := 0;
 IF (DataLength > 512) OR (Action < 1) OR (Action > 5) THEN
  LocalError := 6007;
 ELSIF (Action <> 5) AND (CB_Request[Channel] <> 0) AND ((CB_Feedback[Channel] <> CB_Request[Channel]) OR CB_Busy[Channel]) THEN
  LocalError := 6006;
 ELSIF CB_Request[Channel] = ULINT#18446744073709551615 THEN
  LocalError := 6009;
 ELSE
  CB_Request[Channel] := CB_Request[Channel] + 1;
  RequestId := CB_Request[Channel]; OwnedChannel := Channel;
  CB_Action[Channel] := Action; CB_Function[Channel] := FunctionCode; CB_Unit[Channel] := UnitId;
  CB_Address[Channel] := Address; CB_Quantity[Channel] := Quantity;
  CB_ReadAddress[Channel] := ReadAddress; CB_ReadQuantity[Channel] := ReadQuantity; CB_Length[Channel] := DataLength;
  Base := (UINT_TO_DINT(Channel) - 1) * 512;
  FOR I := 0 TO 511 DO CB_Data[Base + I] := Data[I]; END_FOR;
  Busy := TRUE; Done := FALSE; Error := FALSE; Unknown := FALSE; Acknowledged := FALSE; ReceivedLength := 0;
 END_IF;
END_IF;
Previous := Execute;
(* A rejected new edge is the result of that attempted operation. Matching
   feedback from the previous request must not erase its local error. *)
IF (LocalError = 0) AND (OwnedChannel >= 1) AND (OwnedChannel <= 16) AND (RequestId <> 0) THEN
 IF CB_Request[OwnedChannel] <> RequestId THEN
  Busy := FALSE; Done := FALSE; LocalError := 6006; Unknown := TRUE; Acknowledged := FALSE; ReceivedLength := 0;
 ELSIF CB_Feedback[OwnedChannel] = RequestId THEN
  Busy := CB_Busy[OwnedChannel]; Done := CB_Done[OwnedChannel]; LocalError := CB_Error[OwnedChannel];
  Unknown := CB_Unknown[OwnedChannel]; Acknowledged := CB_Acknowledged[OwnedChannel];
  IF CB_ReceivedLength[OwnedChannel] <= 512 THEN
   ReceivedLength := ULINT_TO_UINT(CB_ReceivedLength[OwnedChannel]); Base := (UINT_TO_DINT(OwnedChannel) - 1) * 512;
   FOR I := 0 TO 511 DO ReceivedData[I] := CB_ReceivedData[Base + I]; END_FOR;
  ELSE LocalError := 6004; Done := FALSE; Busy := FALSE; Unknown := TRUE; Acknowledged := FALSE; ReceivedLength := 0;
  END_IF;
 END_IF;
END_IF;
IF LocalError <> 0 THEN ErrorID := LocalError; Error := TRUE; Busy := FALSE; Done := FALSE;
ELSE ErrorID := 0; Error := FALSE;
END_IF;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_COMM_CLOSE
VAR_INPUT Channel : COMM_REF; Execute : BOOL; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Error : BOOL; ErrorID : UDINT; Unknown : BOOL; Acknowledged : BOOL; RequestId : ULINT; ReceivedLength : UINT; ReceivedData : ARRAY[0..511] OF BYTE; END_VAR
VAR Command : CB_Command; END_VAR
Command(Channel := Channel, Execute := Execute, Action := 5);
Busy := Command.Busy; Done := Command.Done; Error := Command.Error; ErrorID := Command.ErrorID; Unknown := Command.Unknown; Acknowledged := Command.Acknowledged;
RequestId := Command.RequestId; ReceivedLength := Command.ReceivedLength; ReceivedData := Command.ReceivedData;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_MB_REQUEST
VAR_INPUT
 Channel : COMM_REF; Execute : BOOL; FunctionCode : UINT := 3; UnitId : UINT := 1;
 Address : UINT; Quantity : UINT := 1; ReadAddress : UINT; ReadQuantity : UINT;
 DataLength : UINT; Data : ARRAY[0..511] OF BYTE;
END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Error : BOOL; ErrorID : UDINT; Unknown : BOOL; Acknowledged : BOOL; RequestId : ULINT; ReceivedLength : UINT; ReceivedData : ARRAY[0..511] OF BYTE; END_VAR
VAR Command : CB_Command; END_VAR
Command(Channel := Channel, Execute := Execute, Action := 1, FunctionCode := FunctionCode, UnitId := UnitId, Address := Address, Quantity := Quantity, ReadAddress := ReadAddress, ReadQuantity := ReadQuantity, DataLength := DataLength, Data := Data);
Busy := Command.Busy; Done := Command.Done; Error := Command.Error; ErrorID := Command.ErrorID; Unknown := Command.Unknown; Acknowledged := Command.Acknowledged;
RequestId := Command.RequestId; ReceivedLength := Command.ReceivedLength; ReceivedData := Command.ReceivedData;
END_FUNCTION_BLOCK

FUNCTION_BLOCK WB_TCP_CONNECT
VAR_INPUT Channel : COMM_REF; Execute : BOOL; DataLength : UINT; Data : ARRAY[0..511] OF BYTE; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Error : BOOL; ErrorID : UDINT; Unknown : BOOL; Acknowledged : BOOL; RequestId : ULINT; ReceivedLength : UINT; ReceivedData : ARRAY[0..511] OF BYTE; END_VAR
VAR Command : CB_Command; END_VAR
Command(Channel := Channel, Execute := Execute, Action := 2, DataLength := DataLength, Data := Data);
Busy := Command.Busy; Done := Command.Done; Error := Command.Error; ErrorID := Command.ErrorID; Unknown := Command.Unknown; Acknowledged := Command.Acknowledged;
RequestId := Command.RequestId; ReceivedLength := Command.ReceivedLength; ReceivedData := Command.ReceivedData;
END_FUNCTION_BLOCK
FUNCTION_BLOCK WB_TCP_SEND
VAR_INPUT Channel : COMM_REF; Execute : BOOL; DataLength : UINT; Data : ARRAY[0..511] OF BYTE; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Error : BOOL; ErrorID : UDINT; Unknown : BOOL; Acknowledged : BOOL; RequestId : ULINT; ReceivedLength : UINT; ReceivedData : ARRAY[0..511] OF BYTE; END_VAR
VAR Command : CB_Command; END_VAR
Command(Channel := Channel, Execute := Execute, Action := 3, DataLength := DataLength, Data := Data);
Busy := Command.Busy; Done := Command.Done; Error := Command.Error; ErrorID := Command.ErrorID; Unknown := Command.Unknown; Acknowledged := Command.Acknowledged;
RequestId := Command.RequestId; ReceivedLength := Command.ReceivedLength; ReceivedData := Command.ReceivedData;
END_FUNCTION_BLOCK
FUNCTION_BLOCK WB_TCP_RECEIVE
VAR_INPUT Channel : COMM_REF; Execute : BOOL; DataLength : UINT; Data : ARRAY[0..511] OF BYTE; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Error : BOOL; ErrorID : UDINT; Unknown : BOOL; Acknowledged : BOOL; RequestId : ULINT; ReceivedLength : UINT; ReceivedData : ARRAY[0..511] OF BYTE; END_VAR
VAR Command : CB_Command; END_VAR
Command(Channel := Channel, Execute := Execute, Action := 4, DataLength := DataLength, Data := Data);
Busy := Command.Busy; Done := Command.Done; Error := Command.Error; ErrorID := Command.ErrorID; Unknown := Command.Unknown; Acknowledged := Command.Acknowledged;
RequestId := Command.RequestId; ReceivedLength := Command.ReceivedLength; ReceivedData := Command.ReceivedData;
END_FUNCTION_BLOCK
FUNCTION_BLOCK WB_TCP_CLOSE
VAR_INPUT Channel : COMM_REF; Execute : BOOL; DataLength : UINT; Data : ARRAY[0..511] OF BYTE; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Error : BOOL; ErrorID : UDINT; Unknown : BOOL; Acknowledged : BOOL; RequestId : ULINT; ReceivedLength : UINT; ReceivedData : ARRAY[0..511] OF BYTE; END_VAR
VAR Command : CB_Command; END_VAR
Command(Channel := Channel, Execute := Execute, Action := 5, DataLength := DataLength, Data := Data);
Busy := Command.Busy; Done := Command.Done; Error := Command.Error; ErrorID := Command.ErrorID; Unknown := Command.Unknown; Acknowledged := Command.Acknowledged;
RequestId := Command.RequestId; ReceivedLength := Command.ReceivedLength; ReceivedData := Command.ReceivedData;
END_FUNCTION_BLOCK
FUNCTION_BLOCK WB_SERIAL_SEND
VAR_INPUT Channel : COMM_REF; Execute : BOOL; DataLength : UINT; Data : ARRAY[0..511] OF BYTE; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Error : BOOL; ErrorID : UDINT; Unknown : BOOL; Acknowledged : BOOL; RequestId : ULINT; ReceivedLength : UINT; ReceivedData : ARRAY[0..511] OF BYTE; END_VAR
VAR Command : CB_Command; END_VAR
Command(Channel := Channel, Execute := Execute, Action := 3, DataLength := DataLength, Data := Data);
Busy := Command.Busy; Done := Command.Done; Error := Command.Error; ErrorID := Command.ErrorID; Unknown := Command.Unknown; Acknowledged := Command.Acknowledged;
RequestId := Command.RequestId; ReceivedLength := Command.ReceivedLength; ReceivedData := Command.ReceivedData;
END_FUNCTION_BLOCK
FUNCTION_BLOCK WB_SERIAL_RECEIVE
VAR_INPUT Channel : COMM_REF; Execute : BOOL; DataLength : UINT; Data : ARRAY[0..511] OF BYTE; END_VAR
VAR_OUTPUT Busy : BOOL; Done : BOOL; Error : BOOL; ErrorID : UDINT; Unknown : BOOL; Acknowledged : BOOL; RequestId : ULINT; ReceivedLength : UINT; ReceivedData : ARRAY[0..511] OF BYTE; END_VAR
VAR Command : CB_Command; END_VAR
Command(Channel := Channel, Execute := Execute, Action := 4, DataLength := DataLength, Data := Data);
Busy := Command.Busy; Done := Command.Done; Error := Command.Error; ErrorID := Command.ErrorID; Unknown := Command.Unknown; Acknowledged := Command.Acknowledged;
RequestId := Command.RequestId; ReceivedLength := Command.ReceivedLength; ReceivedData := Command.ReceivedData;
END_FUNCTION_BLOCK

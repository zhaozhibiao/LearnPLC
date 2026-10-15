PROGRAM MotionScanFixture
VAR
    Path : MC_ExecuteContinuousPath;
    OtherPath : MC_ExecuteContinuousPath;
    Execute : BOOL;
    Stop : BOOL;
END_VAR
Path(Execute := Execute, Stop := Stop, PathId := ULINT#7, Version := ULINT#1,
     ContentHash := 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
(* This non-owning FB must never clear the first FB's Stop latch. *)
OtherPath(Execute := FALSE, Stop := FALSE, PathId := ULINT#7, Version := ULINT#1,
     ContentHash := 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
END_PROGRAM

CONFIGURATION Config0
RESOURCE Res0 ON PLC
TASK MainTask(INTERVAL := T#10ms, PRIORITY := 0);
PROGRAM Instance0 WITH MainTask : MotionScanFixture;
END_RESOURCE
END_CONFIGURATION

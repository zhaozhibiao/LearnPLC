PROGRAM Main
VAR
    RunPath : MC_ExecuteContinuousPath;
    Trigger : BOOL := FALSE;
    Counter : DINT;
END_VAR
Counter := Counter + 1;
Trigger := Counter >= 3;
RunPath(Execute := Trigger, Stop := FALSE, PathId := ULINT#6148914690377528115, Version := ULINT#1, ContentHash := '86f122386e0214ba642d4b669a804e8894e6df36250418b968e5637f92ab15be');
END_PROGRAM

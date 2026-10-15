PROGRAM AxisFixture
VAR Power : MC_Power; Mover : MC_MoveAbsolute; Stopper : MC_Stop; Other : MC_MoveVelocity; Reset : MC_Reset; Home : MC_Home; Status : MC_ReadAxisStatus; Position : MC_ReadActualPosition; Velocity : MC_ReadActualVelocity;
Enable : BOOL; Execute : BOOL; StopExecute : BOOL; OtherExecute : BOOL; END_VAR
Power(Axis := 1, Enable := Enable);
Mover(Axis := 1, Execute := Execute, Position := 12.0, Velocity := 2.0);
Stopper(Axis := 1, Execute := StopExecute);
Other(Axis := 1, Execute := OtherExecute, Velocity := 2.0);
END_PROGRAM
CONFIGURATION Config0
RESOURCE Res0 ON PLC
TASK Main(INTERVAL := T#10ms, PRIORITY := 0);
PROGRAM Instance0 WITH Main : AxisFixture;
END_RESOURCE
END_CONFIGURATION


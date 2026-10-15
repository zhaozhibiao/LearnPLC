PROGRAM Main
VAR
 EnableAxis : BOOL;
 ExecuteReset : BOOL;
 ExecuteHome : BOOL;
 ExecuteMove : BOOL;
 ExecuteJog : BOOL;
 ExecuteStop : BOOL;
 Power : MC_Power;
 Reset : MC_Reset;
 Home : MC_Home;
 MoveAxis : MC_MoveAbsolute;
 Jog : MC_MoveVelocity;
 Stop : MC_Stop;
 Status : MC_ReadAxisStatus;
 Position : MC_ReadActualPosition;
 Velocity : MC_ReadActualVelocity;
END_VAR
Power(Axis := AXIS_X, Enable := EnableAxis);
Reset(Axis := AXIS_X, Execute := ExecuteReset);
Home(Axis := AXIS_X, Execute := ExecuteHome AND Power.Status);
MoveAxis(Axis := AXIS_X, Execute := ExecuteMove AND Power.Status, Position := 1.0, Velocity := 1.0);
Jog(Axis := AXIS_X, Execute := ExecuteJog AND Power.Status, Velocity := 1.0);
Stop(Axis := AXIS_X, Execute := ExecuteStop);
Status(Axis := AXIS_X, Enable := TRUE);
Position(Axis := AXIS_X, Enable := TRUE);
Velocity(Axis := AXIS_X, Enable := TRUE);
END_PROGRAM

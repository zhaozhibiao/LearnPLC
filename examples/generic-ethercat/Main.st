PROGRAM Main
VAR
    Status : MC_ReadAxisStatus;
    Position : MC_ReadActualPosition;
    Velocity : MC_ReadActualVelocity;
END_VAR

(* Read-only commissioning example. No Enable/Home/Move/Stop command. *)
Status(Axis := AXIS_IS620N, Enable := TRUE);
Position(Axis := AXIS_IS620N, Enable := TRUE);
Velocity(Axis := AXIS_IS620N, Enable := TRUE);
END_PROGRAM

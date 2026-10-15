using StWorkbench.Core;

public static class AxisBindingTests
{
    public static void Run()
    {
        AxisConfiguration[] axes = [new("Y", "Axis2", "mm", -100, 100), new("X", "Axis1", "mm", -100, 100)];
        TaskSnapshot[] tasks = [new("Cyclic", Guid.NewGuid(), 10, true)];
        SourceDocument[] Sources(string axis = "AXIS_X") => [new(Guid.NewGuid(), "Main.st", "PROGRAM Main\nVAR\n Move : MC_MoveAbsolute;\nEND_VAR\nMove(Axis := " + axis + ", Execute := TRUE, Position := 1, Velocity := 1);\nEND_PROGRAM\n")];
        Check(AxisControlBinding.UsesAxisControl(Sources()), "executable axis block detected");
        Check(!AxisControlBinding.UsesAxisControl([new(Guid.NewGuid(), "Main.st", "PROGRAM Main\n(* MC_Power *)\nEND_PROGRAM\n")]), "comment does not request axis runtime");
        Check(AxisControlBinding.Validate(Sources(), axes, tasks).Count == 0, "configured static binding");
        foreach (var value in new[] { "AXIS_Missing", "VariableAxis", "0", "AXIS_X + 1" }) Check(AxisControlBinding.Validate(Sources(value), axes, tasks).Count > 0, "invalid or dynamic axis reference");
        Check(AxisControlBinding.Validate(Sources(), [], tasks).Count > 0, "axis required");
        Check(AxisControlBinding.Validate(Sources(), axes, [tasks[0] with { IntervalMilliseconds = 2000 }]).Count > 0, "bounded PLC scan watchdog period");
        var constants = AxisControlBinding.Constants(axes);
        Check(constants.Contains("AXIS_X : AXIS_REF := 1;") && constants.Contains("AXIS_Y : AXIS_REF := 2;"), "deterministic axis constants");
        var array = Sources()[0] with { Text = "PROGRAM Main\nVAR\n Moves : ARRAY[0..2] OF MC_MoveAbsolute;\nEND_VAR\nEND_PROGRAM\n" };
        Check(AxisControlBinding.Validate([array], axes, tasks).Count > 0, "unverified arrays refused");
        var shadow = Sources()[0] with { Text = Sources()[0].Text.Replace("Move :", "AXIS_X : UINT;\n Move :", StringComparison.Ordinal) };
        Check(AxisControlBinding.Validate([shadow], axes, tasks).Count > 0, "shadowed axis constant refused");
        var function = Sources()[0] with { Text = "FUNCTION Action : BOOL\nVAR\n Power : MC_Power;\nEND_VAR\nPower(Axis := AXIS_X, Enable := TRUE);\nEND_FUNCTION\n" };
        Check(AxisControlBinding.Validate([function], axes, tasks).Count > 0, "stateful axis instance in function refused");
        var wrapper = function with { Text = "FUNCTION Action : BOOL\nVAR\n Helper : OuterAxis;\nEND_VAR\nHelper();\nEND_FUNCTION\nFUNCTION_BLOCK OuterAxis\nVAR\n Inner : AxisWrapper;\nEND_VAR\nInner();\nEND_FUNCTION_BLOCK\nFUNCTION_BLOCK AxisWrapper\nVAR\n Power : MC_Power;\nEND_VAR\nPower(Axis := AXIS_X, Enable := TRUE);\nEND_FUNCTION_BLOCK\n" };
        Check(AxisControlBinding.Validate([wrapper], axes, tasks).Count > 0, "nested axis wrapper in function refused");
        var aliasArray = wrapper with { Text = "TYPE WrapperAlias : OuterAxis; END_TYPE\n" + wrapper.Text.Replace("Helper : OuterAxis;", "Helper : ARRAY[0..1] OF WrapperAlias;", StringComparison.Ordinal) };
        Check(AxisControlBinding.Validate([aliasArray], axes, tasks).Count > 0, "axis wrapper alias array in function refused");
        var structure = wrapper with { Text = "TYPE Holder : STRUCT Wrapper : OuterAxis; END_STRUCT END_TYPE\n" + wrapper.Text.Replace("Helper : OuterAxis;", "Helper : Holder;", StringComparison.Ordinal).Replace("Helper();", "Helper.Wrapper();", StringComparison.Ordinal) };
        Check(AxisControlBinding.Validate([structure], axes, tasks).Count > 0, "axis wrapper structure in function refused");
        Console.WriteLine("PASS axis library detection, static binding, names, bounded period, constants and shadow/array rejection");
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
}

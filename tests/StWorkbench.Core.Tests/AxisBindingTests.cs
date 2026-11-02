using StWorkbench.Core;

public static class AxisBindingTests
{
    public static void Run()
    {
        AxisConfiguration[] axes = [new("Y", "Axis2", "mm", -100, 100), new("X", "Axis1", "mm", -100, 100)];
        TaskSnapshot[] tasks = [new("Cyclic", Guid.NewGuid(), 10, true)];
        SourceDocument[] Sources(string axis = "AXIS_X") => [new(Guid.NewGuid(), "Main.st", "PROGRAM Main\nVAR\n Move : MC_MoveAbsolute;\nEND_VAR\nMove(Axis := " + axis + ", Execute := TRUE, Position := 1, Velocity := 1);\nEND_PROGRAM\n")];
        Check(AxisControlBinding.UsesAxisControl(Sources()), "executable axis block detected");
        var statusSource = Sources()[0] with { Text = "PROGRAM Main\nVAR Reader : MC_ReadStatus; END_VAR\nReader(Axis := AXIS_X, Enable := FALSE);\nEND_PROGRAM" };
        Check(AxisControlBinding.UsesAxisControl([statusSource]), "canonical status reader detected");
        Check(AxisControlBinding.Validate([statusSource], [], tasks).Count > 0, "canonical reader requires configured axes");
        Check(AxisControlBinding.Validate([statusSource with { Text = statusSource.Text.Replace("AXIS_X", "DynamicAxis", StringComparison.Ordinal) }], axes, tasks).Count > 0, "canonical reader rejects dynamic axis");
        Check(AxisControlBinding.Validate([statusSource with { Text = "FUNCTION_BLOCK MC_ReadStatus\nEND_FUNCTION_BLOCK" }], axes, tasks).Count > 0, "canonical reader name reserved");
        Check(AxisControlBinding.Validate([statusSource with { Text = statusSource.Text.Replace("PROGRAM Main", "FUNCTION Main : BOOL", StringComparison.Ordinal).Replace("END_PROGRAM", "END_FUNCTION", StringComparison.Ordinal) }], axes, tasks).Count > 0, "canonical reader requires persistent instance");
        Check(AxisControlBinding.BlockNames.Contains("MC_ReadAxisStatus"), "legacy reader remains bound");
        var templates = AxisInstructionCatalog.Create(axes).ToArray();
        Check(templates.Length == axes.Length * 9, "one canonical status reader per axis");
        Check(templates.Any(t => t.Id == "Axis_X_MC_ReadStatus"), "canonical status reader available for insertion");
        Check(!templates.Any(t => t.Id == "Axis_X_MC_ReadAxisStatus"), "legacy reader does not duplicate desktop catalog");
        foreach (var template in templates)
        {
            Check(template.Code.Contains("Axis := AXIS_", StringComparison.Ordinal), "every template statically binds an axis");
            if (template.Id.Contains("_MC_Read", StringComparison.Ordinal))
                Check(template.Code.Contains("Enable := TRUE", StringComparison.Ordinal) && template.Declarations.Length == 1, "read-only templates immediately read feedback without extra input variables");
            else
                Check(template.Declarations.Any(d => d.EndsWith(": BOOL := FALSE;", StringComparison.Ordinal)), "motion and power templates have an inactive operator input");
            Check(!template.Code.Contains("=>", StringComparison.Ordinal) && !template.Code.Contains("IF ", StringComparison.Ordinal), "no copied outputs or external state machine");
            Check(template.Description.Contains("{p}.", StringComparison.Ordinal) && template.Description.Contains("ErrorID", StringComparison.Ordinal), "outputs documented as instance members");
            var text = "PROGRAM Main\nVAR\nEND_VAR\nEND_PROGRAM\n";
            var inserted = StSnippetInsertion.Insert(text, text.IndexOf("END_PROGRAM", StringComparison.Ordinal), template);
            var declarations = new StLanguageService().Index([new(Guid.NewGuid(), "Main.st", inserted.Text)]).Where(s => s.Kind.StartsWith("VAR", StringComparison.Ordinal)).ToArray();
            Check(declarations.Length == template.Declarations.Length, "insertion declares only instance and inputs");
        }
        var powerTemplate = templates.Single(t => t.Id == "Axis_X_MC_Power");
        Check(powerTemplate.Description.Contains("{p}.Status", StringComparison.Ordinal) && !powerTemplate.Description.Contains("{p}.Done", StringComparison.Ordinal), "power documents live Status without Done");
        var velocityTemplate = templates.Single(t => t.Id == "Axis_X_MC_MoveVelocity");
        Check(velocityTemplate.Description.Contains("{p}.InVelocity", StringComparison.Ordinal) && !velocityTemplate.Description.Contains("{p}.Done", StringComparison.Ordinal), "velocity documents InVelocity without completed-position Done");
        Check(velocityTemplate.Code.Contains("Acceleration := {p}Acceleration", StringComparison.Ordinal) && velocityTemplate.Code.Contains("Deceleration := {p}Deceleration", StringComparison.Ordinal) && velocityTemplate.Code.Contains("Jerk := {p}Jerk", StringComparison.Ordinal), "verified phase2 velocity exposes dynamic pins");
        SourceDocument Dynamic(string body) => new(Guid.NewGuid(), "Main.st", "PROGRAM Main\nVAR Move : MC_MoveAbsolute; Other : DINT; END_VAR\n" + body + "\nEND_PROGRAM");
        Check(AxisControlBinding.UsesDynamicProfile([Dynamic("Move(Axis := AXIS_X, Acceleration := 1.0);")]), "actual named acceleration selects v2");
        Check(!AxisControlBinding.UsesDynamicProfile([Dynamic("(* Move(Acceleration := 1); *) Other(Acceleration := 1); Move(Axis := AXIS_X, Position := Other(Jerk := 1));")]), "comments unrelated calls and nested args retain v1");
        Check(!AxisControlBinding.UsesDynamicProfile([Dynamic("Other := 'Move(Jerk := 1)';")]), "strings retain v1");
        Check(AxisControlBinding.Validate([Dynamic("Move(Axis := AXIS_X, Jerk := 0);")], axes, tasks).Any(d => d.Message.Contains("explicit", StringComparison.Ordinal)), "v2 missing defaults rejected");
        var configuredAxes = axes.Select(a => a with { MaximumAcceleration = 2, MaximumDeceleration = 3, MaximumJerk = 0 }).ToArray();
        Check(AxisControlBinding.Validate([Dynamic("Move(Axis := AXIS_X, Jerk := 0);")], configuredAxes, tasks).Count == 0, "explicit jerk zero accepted");
        foreach (var invalidDefault in new double?[] { null, -1, double.NaN, double.PositiveInfinity })
        {
            var invalidAxes = configuredAxes.Select(a => a with { MaximumJerk = invalidDefault }).ToArray();
            Check(AxisControlBinding.Validate([Dynamic("Move(Axis := AXIS_X, Jerk := 0);")], invalidAxes, tasks).Count > 0, "unknown negative or nonfinite jerk default rejected");
        }
        foreach (var invalidDefault in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity })
        {
            var invalidAxes = configuredAxes.Select(a => a with { MaximumAcceleration = invalidDefault }).ToArray();
            Check(AxisControlBinding.Validate([Dynamic("Move(Axis := AXIS_X, Acceleration := 0);")], invalidAxes, tasks).Count > 0, "unknown nonpositive or nonfinite acceleration default rejected");
        }
        var aliasSource = Dynamic("Move(Axis := AXIS_X, jErK := 0);") with { Text = "TYPE MyMove : MC_MoveAbsolute; END_TYPE\nPROGRAM Main\nVAR Move : MyMove; END_VAR\nMove(Axis := AXIS_X, jErK := 0);\nEND_PROGRAM" };
        Check(AxisControlBinding.UsesDynamicProfile([aliasSource]), "aliased actual FB and case insensitive parameter select v2");
        var unrelatedOwner = Dynamic("Other(Jerk := 1);") with { Text = "PROGRAM OtherMain\nVAR Move : DINT; END_VAR\nMove(Jerk := 1);\nEND_PROGRAM" };
        Check(!AxisControlBinding.UsesDynamicProfile([Dynamic("Move(Axis := AXIS_X, Position := 1);"), unrelatedOwner]), "unrelated POU same identifier does not select v2");
        var moveAbsTemplate = templates.Single(t => t.Id == "Axis_X_MC_MoveAbsolute");
        Check(moveAbsTemplate.Code.Contains("Acceleration := {p}Acceleration", StringComparison.Ordinal) && moveAbsTemplate.Code.Contains("Deceleration := {p}Deceleration", StringComparison.Ordinal) && moveAbsTemplate.Code.Contains("Jerk := {p}Jerk", StringComparison.Ordinal), "move absolute template includes dynamic acceleration, deceleration and jerk inputs");
        Check(moveAbsTemplate.Declarations.Any(d => d.Contains("{p}Acceleration : LREAL := 0.0;", StringComparison.Ordinal)) && moveAbsTemplate.Declarations.Any(d => d.Contains("{p}Deceleration : LREAL := 0.0;", StringComparison.Ordinal)) && moveAbsTemplate.Declarations.Any(d => d.Contains("{p}Jerk : LREAL := 0.0;", StringComparison.Ordinal)), "move absolute template declares default zero dynamic profile variables");
        var stopTemplate = templates.Single(t => t.Id == "Axis_X_MC_Stop");
        Check(stopTemplate.Code.Contains("Deceleration :=", StringComparison.Ordinal) && stopTemplate.Code.Contains("Jerk :=", StringComparison.Ordinal) && !stopTemplate.Code.Contains("Acceleration :=", StringComparison.Ordinal), "stop exposes D/J only");
        foreach (var type in new[] { "MC_MoveVelocity", "MC_Stop" })
        {
            var dynamicVelocity = Dynamic("Move(Axis := AXIS_X, Jerk := 0);") with { Text = Dynamic("Move(Axis := AXIS_X, Jerk := 0);").Text.Replace("MC_MoveAbsolute", type, StringComparison.Ordinal) };
            Check(AxisControlBinding.UsesDynamicProfile([dynamicVelocity]) && AxisControlBinding.UsesDynamicVelocityProfile([dynamicVelocity]), "jog/stop named dynamics require velocity capability");
            Check(AxisControlBinding.Validate([dynamicVelocity], axes, tasks).Count > 0 && AxisControlBinding.Validate([dynamicVelocity], configuredAxes, tasks).Count == 0, "phase2 defaults are explicit");
        }
        Check(!AxisControlBinding.UsesDynamicVelocityProfile([Dynamic("Move(Axis := AXIS_X, Jerk := 0);")]), "absolute move does not require velocity capability");
        var inheritedStop = Dynamic("Move(Axis := AXIS_X, Jerk := 0); StopAxis(Axis := AXIS_X, Execute := FALSE);") with { Text = Dynamic("Move(Axis := AXIS_X, Jerk := 0); StopAxis(Axis := AXIS_X, Execute := FALSE);").Text.Replace("Other : DINT;", "StopAxis : MC_Stop;", StringComparison.Ordinal) };
        Check(AxisControlBinding.UsesDynamicVelocityProfile([inheritedStop]), "v2 absolute move plus omitted Stop dynamics requires velocity profile");
        Check(!AxisControlBinding.UsesDynamicVelocityProfile([inheritedStop with { Text = inheritedStop.Text.Replace(", Jerk := 0", "", StringComparison.Ordinal) }]), "all-omitted legacy program remains v1");

        var statusTemplate = templates.Single(t => t.Id == "Axis_X_MC_ReadStatus");
        Check(statusTemplate.Description.Contains("SynchronizedMotion", StringComparison.Ordinal) && statusTemplate.Description.Contains("FALSE", StringComparison.Ordinal), "unsupported synchronized state is explicitly false");
        var compatibleTemplates = AxisInstructionCatalog.Create(axes, includeCompatibilityAliases: true).ToArray();
        Check(compatibleTemplates.Length == axes.Length * 10 && compatibleTemplates.Select(t => t.Id).Distinct().Count() == compatibleTemplates.Length, "MCP-compatible catalog has unique canonical and legacy template IDs");
        var legacyTemplate = compatibleTemplates.Single(t => t.Id == "Axis_X_MC_ReadAxisStatus");
        Check(legacyTemplate.Declarations.Contains("{p} : MC_ReadAxisStatus;") && legacyTemplate.Description.Contains("{p}.Homed", StringComparison.Ordinal) && legacyTemplate.Description.Contains("扩展", StringComparison.Ordinal), "legacy template preserves old block and documents Homed extension");
        Check(legacyTemplate.Code.Contains("Enable := TRUE", StringComparison.Ordinal) && legacyTemplate.Declarations.Length == 1, "legacy reader remains immediately readable for existing generators");
        foreach (var name in new[] { "MC_Reset", "MC_Home", "MC_MoveAbsolute", "MC_Stop" })
        {
            var template = templates.Single(t => t.Id == "Axis_X_" + name);
            foreach (var output in new[] { "Done", "Busy", "Active", "CommandAborted", "Error", "ErrorID" })
                Check(template.Description.Contains("{p}." + output, StringComparison.Ordinal), "command documents actual output " + output);
        }
        foreach (var name in new[] { "MC_ReadStatus", "MC_ReadActualPosition", "MC_ReadActualVelocity" })
        {
            var template = templates.Single(t => t.Id == "Axis_X_" + name);
            foreach (var output in new[] { "Valid", "Busy", "Error", "ErrorID" })
                Check(template.Description.Contains("{p}." + output, StringComparison.Ordinal), "reader documents actual output " + output);
        }
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

using StWorkbench.Core;

public static class ResourceInsertionTests
{
    public static void Run()
    {
        var target = "PROGRAM Main\nVAR\n    Old : Motor;\n    Result : INT;\nEND_VAR\n\nResult := 1;\nEND_PROGRAM\n";
        var function = new ProjectItem { Name = "Sum", Kind = ItemKind.Function };
        var block = new ProjectItem { Name = "Motor", Kind = ItemKind.FunctionBlock };
        var globals = new ProjectItem { Name = "Globals", Kind = ItemKind.GlobalVariables };
        var types = new ProjectItem { Name = "Settings", Kind = ItemKind.DataType };
        var f = new SourceDocument(function.Id, "Sum.st", "FUNCTION Sum : INT\nVAR_INPUT\n A, B : INT;\nEND_VAR\nSum := A + B;\nEND_FUNCTION\n");
        var b = new SourceDocument(block.Id, "Motor.st", "FUNCTION_BLOCK Motor\nVAR_INPUT\n Enable : BOOL;\n Values : ARRAY[0..2] OF INT;\n Label : STRING[20];\nEND_VAR\nVAR_IN_OUT\n Count : INT;\nEND_VAR\nVAR_OUTPUT\n Done : BOOL;\nEND_VAR\nDone := Enable;\nEND_FUNCTION_BLOCK\n");
        var g = new SourceDocument(globals.Id, "Globals.st", "VAR_GLOBAL\n Start : BOOL;\n Counter : INT;\nEND_VAR\n");
        var t = new SourceDocument(types.Id, "Settings.st", "TYPE Settings : STRUCT\n Value : INT;\nEND_STRUCT;\nOther : INT;\nEND_TYPE\n");
        SourceDocument[] docs = [f, b, g, t];
        StInsertionResult Insert(ProjectItem item, SourceDocument doc, string? member = null, string? instance = null, string? text = null, int? offset = null)
            => StResourceInsertion.Insert(text ?? target, offset ?? (text ?? target).IndexOf("END_PROGRAM", StringComparison.Ordinal), item, doc, docs, member, instance);
        var call = Insert(function, f);
        Check(call.Text.Contains("Sum(A :=") && call.Text.Contains("B :=") && call.Text.Contains("Result : INT;"), "function signature and return declaration");
        var resultInput = f with { Text = f.Text.Replace("A, B : INT;", "Result : INT;", StringComparison.Ordinal).Replace("A + B", "Result", StringComparison.Ordinal) };
        var resultCall = Insert(function, resultInput);
        Check(resultCall.Text.Split("_Result : INT;").Length == 2, "return local must not collide with Result parameter");
        var namedReturn = Insert(function, f with { Text = f.Text.Replace("FUNCTION Sum : INT", "FUNCTION Sum : Variation", StringComparison.Ordinal) });
        Check(namedReturn.Text.Contains(" : Variation;"), "type name beginning VAR is not a section keyword");
        var endName = Insert(function, f with { Text = f.Text.Replace("A, B : INT;", "A, B : END_Status;", StringComparison.Ordinal) });
        Check(endName.Text.Contains(" : END_Status;"), "type name containing END_ is not a terminator");
        var multilineReturn = Insert(function, f with { Text = f.Text.Replace("FUNCTION Sum : INT", "FUNCTION Sum : ARRAY\n[0..2] OF INT", StringComparison.Ordinal) });
        Check(multilineReturn.Text.Contains(" : ARRAY [0 .. 2] OF INT;"), "multiline return type preserved");
        var created = Insert(block, b);
        Check(created.Text.Contains(" : Motor;") && created.Text.Contains("Enable :=") && created.Text.Contains("Count :=") && created.Text.Contains("ARRAY [0 .. 2] OF INT") && created.Text.Contains("STRING [20]"), "full interface types");
        Check(!created.Text.Contains("Old(Enable :="), "new instance default");
        var reused = Insert(block, b, instance: "Old");
        Check(reused.Text.Contains("Old(Enable :=") && reused.Text.Split(" : Motor;").Length == 2, "explicit instance reuse");
        Reject(() => Insert(block, b, instance: "Result"), "wrong type instance");
        Reject(() => Insert(block, b, text: "FUNCTION F : INT\nVAR\nEND_VAR\nEND_FUNCTION\n", offset: 39), "stateful block in function");
        var variable = Insert(globals, g, "Start", text: target.Replace("Result := 1;", "Result := ;"), offset: target.IndexOf("1;", StringComparison.Ordinal));
        Check(variable.Text.Contains("Result := Start;"), "global variable reference");
        Reject(() => Insert(globals, g), "global collection cannot be called");
        Reject(() => Insert(globals, g, "Missing"), "missing variable");
        var located = g with { Text = "VAR_GLOBAL\n PhysicalInput AT %IX0.0 : BOOL;\nEND_VAR\n" };
        var locatedTarget = target.Replace("Result := 1;", "Result := ;", StringComparison.Ordinal);
        var locatedReference = Insert(globals, located, "PhysicalInput", text: locatedTarget, offset: locatedTarget.IndexOf(";", locatedTarget.IndexOf("Result :=", StringComparison.Ordinal), StringComparison.Ordinal));
        Check(locatedReference.Text.Contains("Result := PhysicalInput;"), "located global reference");
        Reject(() => Insert(globals, g, "Start", text: target.Replace("Result : INT;", "Start : BOOL;")), "local shadow");
        var header = target.Replace("PROGRAM Main", "PROGRAM Main ", StringComparison.Ordinal);
        Reject(() => Insert(globals, g, "Start", text: header, offset: header.IndexOf('\n')), "global rejected in POU header");
        var declaration = Insert(types, t, "Other", offset: target.IndexOf("Old :", StringComparison.Ordinal));
        Check(declaration.Text.Contains(" : Other;") && !declaration.Text.Contains("Other("), "type declaration");
        Reject(() => Insert(types, t, "Settings"), "datatype rejected in executable area");
        Reject(() => Insert(function, f, text: target.Replace("Result := 1;", "(* comment *)"), offset: target.IndexOf("Result :=", StringComparison.Ordinal) + 4), "comment insertion");
        var doubleCall = StResourceInsertion.Insert(call.Text, call.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal), function, f, docs);
        Check(doubleCall.InstancePrefix != call.InstancePrefix, "unique repeated insertion");
        Console.WriteLine("PASS project resource calls, instance reuse, full interface types, globals, declaration placement and rejection");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
    private static void Reject(Action action, string message) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("FAIL: " + message); }
}

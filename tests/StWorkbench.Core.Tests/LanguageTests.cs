using StWorkbench.Core;
public static class LanguageTests
{
    public static void Run()
    {
        var source = "(* VAR fake : BOOL; END_VAR *)\nFUNCTION_BLOCK Move\nVAR_INPUT Axis : DINT; Execute : BOOL; END_VAR\nVAR_OUTPUT Done : BOOL; END_VAR\nVAR label : STRING := 'VAR ghost : INT;'; END_VAR\nEND_FUNCTION_BLOCK";
        var service = new StLanguageService();
        var inlineSource = "PROGRAM Main\nVAR Count : DINT; A : Move; B : Move; END_VAR\nCount := Count + 1; // Count\nA.Done := TRUE;\nLabel := 'Count'; (* Count *)\nEND_PROGRAM\nFUNCTION_BLOCK Move\nVAR_OUTPUT Done : BOOL; END_VAR\nDone := FALSE; END_FUNCTION_BLOCK";
        var inlineDoc = new SourceDocument(Guid.NewGuid(), "Main.st", inlineSource);
        var contexts = service.MonitorContexts([inlineDoc], "Main", ["INSTANCE0.COUNT", "INSTANCE0.A.DONE", "INSTANCE0.B.DONE"]);
        if (contexts.Count != 3) throw new Exception("FAIL: distinct program and FB monitor contexts");
        var references = service.MonitorReferences(inlineDoc, contexts.Single(c => c.Path == "INSTANCE0"), ["INSTANCE0.COUNT", "INSTANCE0.A.DONE", "INSTANCE0.B.DONE"]);
        if (references.Count(r => r.Path == "INSTANCE0.COUNT") != 3 || !references.Any(r => r.Path == "INSTANCE0.A.DONE") || references.Any(r => r.Path == "INSTANCE0.B.DONE")) throw new Exception("FAIL: lexical inline values must exclude strings/comments and other owners");
        var fbRefs = service.MonitorReferences(inlineDoc, contexts.Single(c => c.Path == "INSTANCE0.B"), ["INSTANCE0.COUNT", "INSTANCE0.A.DONE", "INSTANCE0.B.DONE"]);
        if (fbRefs.Count != 2 || fbRefs.Any(r => r.Path != "INSTANCE0.B.DONE")) throw new Exception("FAIL: explicit FB instance selection");
        var renamed = service.RenameLocalIdentifier("FUNCTION Foo : DINT\nFoo := 1; (* Foo *)\nLabel := 'Foo';\nEND_FUNCTION", "Foo", "Bar");
        if (!renamed.Contains("FUNCTION Bar") || !renamed.Contains("Bar := 1") || !renamed.Contains("(* Foo *)") || !renamed.Contains("'Foo'")) throw new Exception("FAIL: lexical rename declaration/return/comment/string");
        var symbols = service.Index([new SourceDocument(Guid.NewGuid(), "sources/Move.st", source)]);
        if (symbols.Any(s => s.Name is "fake" or "ghost") || !symbols.Any(s => s.Name == "Axis" && s.Type == "DINT")) throw new Exception("FAIL: lexical context");
        if (!service.Complete(symbols, "Ex").Any(s => s.Name == "Execute")) throw new Exception("FAIL: completion");
        if (!service.Signature(symbols, "Move")!.Contains("Axis")) throw new Exception("FAIL: signature");
        if (symbols.First(s => s.Name == "Move").Line != 2) throw new Exception("FAIL: location");
        Console.WriteLine("PASS: comment/string isolation, completion, signature, source location");
        var typed = service.Index([new SourceDocument(Guid.NewGuid(), "sources/Types.st", "TYPE First : INT; Second : ARRAY [1..10] OF REAL; END_TYPE\nFUNCTION_BLOCK Shapes\nVAR_INPUT Points : ARRAY [1..10] OF REAL; END_VAR\nEND_FUNCTION_BLOCK")]);
        if (!typed.Any(s => s.Name == "Second" && s.Kind == "TYPE")) throw new Exception("FAIL: multiple type declarations");
        if (!service.Signature(typed, "Shapes")!.Contains("ARRAY [1..10] OF REAL")) throw new Exception("FAIL: complete array parameter signature");
        Console.WriteLine("PASS: multiple datatypes and array signature");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "tools", "axis-bridge", "axis_control.st"))) repository = repository.Parent;
        if (repository is null) throw new Exception("FAIL: repository for actual axis library symbols");
        var publicSymbols = BuiltinLibraryCatalog.PublicSymbols(repository.FullName, [], null);
        var missingOutputs = new[] { (Owner: "MC_MoveAbsolute", Output: "Done"), (Owner: "MC_ReadStatus", Output: "Stopping"), (Owner: "MC_Power", Output: "Status"), (Owner: "MC_MoveVelocity", Output: "InVelocity"), (Owner: "MC_ReadStatus", Output: "Busy") }
            .Where(output => !publicSymbols.Any(s => s.Owner == output.Owner && s.Name == output.Output && s.Kind == "VAR_OUTPUT")).Select(output => output.Owner + "." + output.Output).ToArray();
        if (missingOutputs.Length > 0) throw new Exception("FAIL: real axis outputs missing from editor: " + string.Join(", ", missingOutputs));
        if (publicSymbols.Any(s => s.Name.StartsWith("AB_", StringComparison.OrdinalIgnoreCase) || s.Owner?.StartsWith("AB_", StringComparison.OrdinalIgnoreCase) == true || s.Name.StartsWith("CB_", StringComparison.OrdinalIgnoreCase) || s.Owner?.StartsWith("CB_", StringComparison.OrdinalIgnoreCase) == true)) throw new Exception("FAIL: internal bridge symbols leaked into public completions");
        if (publicSymbols.Any(s => s.File == "tools/axis-bridge/axis_control.st" && s.Kind is "VAR" or "VAR_GLOBAL")) throw new Exception("FAIL: axis implementation locals leaked through public blocks");
        foreach (var block in new[] { "MC_MoveAbsolute", "MC_MoveVelocity", "MC_Stop" })
        {
            foreach (var input in block == "MC_Stop" ? new[] { "Deceleration", "Jerk" } : new[] { "Acceleration", "Deceleration", "Jerk" })
                if (!publicSymbols.Any(s => s.Owner == block && s.Kind == "VAR_INPUT" && s.Name == input)) throw new Exception("FAIL: verified v2 dynamic pin missing from editor signature: " + block + "." + input);
            if (block == "MC_Stop" && publicSymbols.Any(s => s.Owner == block && s.Kind == "VAR_INPUT" && s.Name == "Acceleration")) throw new Exception("FAIL: unsupported Stop acceleration advertised");
        }
        if (publicSymbols.Any(s => s.File.Contains("axis-bridge", StringComparison.Ordinal) && s.Kind is "VAR" or "VAR_GLOBAL")) throw new Exception("FAIL: v2 implementation locals leaked through public blocks");
        Console.WriteLine("PASS: actual axis public outputs available without private bridge symbols");
    }
}

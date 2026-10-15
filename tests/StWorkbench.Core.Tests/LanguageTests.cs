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
    }
}

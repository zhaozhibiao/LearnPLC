using StWorkbench.Core;

public static class InstructionTests
{
    public static void Run()
    {
        var timer = StInstructionCatalog.All.Single(s => s.Id == "TON");
        var source = "PROGRAM Main\nVAR\n    Existing : BOOL;\nEND_VAR\n(* END_VAR PROGRAM Bogus *)\n\nEND_PROGRAM\n";
        var result = StSnippetInsertion.Insert(source, source.IndexOf("END_PROGRAM", StringComparison.Ordinal), timer);
        if (!result.Text.Contains("SnippetTON1 : TON;") || !result.Text.Contains("SnippetTON1(IN :=") || result.Text.IndexOf("SnippetTON1 : TON;", StringComparison.Ordinal) > result.Text.IndexOf("END_VAR", StringComparison.Ordinal)) throw new Exception("FAIL: timer instance declaration and invocation");
        var second = StSnippetInsertion.Insert(result.Text, result.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal), timer);
        if (!second.Text.Contains("SnippetTON2 : TON;")) throw new Exception("FAIL: unique snippet instances");
        var noVars = "PROGRAM First\nEND_PROGRAM\nPROGRAM Second\nEND_PROGRAM";
        var targeted = StSnippetInsertion.Insert(noVars, noVars.LastIndexOf("END_PROGRAM", StringComparison.Ordinal), timer);
        if (targeted.Text.IndexOf("SnippetTON1 : TON;", StringComparison.Ordinal) < targeted.Text.IndexOf("PROGRAM Second", StringComparison.Ordinal)) throw new Exception("FAIL: declarations added to clicked POU");
        var insideVars = StSnippetInsertion.Insert(source, source.IndexOf("Existing", StringComparison.Ordinal), timer);
        if (insideVars.Text.IndexOf("SnippetTON1(IN :=", StringComparison.Ordinal) < insideVars.Text.IndexOf("END_VAR", StringComparison.Ordinal)) throw new Exception("FAIL: executable snippet must not be inserted in declarations");
        var expressionSource = "PROGRAM Main\nVAR\nCount : DINT;\nEND_VAR\n    Count := Count + 1;\nEND_PROGRAM\n";
        var atToken = StSnippetInsertion.Insert(expressionSource, expressionSource.LastIndexOf("Count", StringComparison.Ordinal) + 2, timer);
        if (!atToken.Text.Contains("    Count := Count + 1;") || !atToken.Text.Contains("    SnippetTON1(IN :=")) throw new Exception("FAIL: drop must preserve complete original statements and indentation");
        var continuation = expressionSource.Replace("Count := Count + 1;", "Count :=\n        Count + 1;");
        try { StSnippetInsertion.Insert(continuation, continuation.LastIndexOf("Count", StringComparison.Ordinal), timer); throw new Exception("FAIL: inserted within multiline expression"); } catch (InvalidOperationException) { }
        var commentedSource = "PROGRAM Main\nVAR\nEND_VAR\n(* comment\ninside comment *)\nEND_PROGRAM\n";
        try { StSnippetInsertion.Insert(commentedSource, commentedSource.IndexOf("inside", StringComparison.Ordinal), timer); throw new Exception("FAIL: inserted into multiline comment"); } catch (InvalidOperationException) { }
        try { StSnippetInsertion.Insert(commentedSource, commentedSource.IndexOf("*)", StringComparison.Ordinal) + 2, timer); throw new Exception("FAIL: closing comment line normalized into comment"); } catch (InvalidOperationException) { }
        var stringSource = "PROGRAM Main\nVAR\nText : STRING;\nEND_VAR\nText := 'hello';\nEND_PROGRAM\n";
        try { StSnippetInsertion.Insert(stringSource, stringSource.IndexOf("hello", StringComparison.Ordinal) + 1, timer); throw new Exception("FAIL: inserted into literal"); } catch (InvalidOperationException) { }
        try { StSnippetInsertion.Insert("TYPE Thing : INT; END_TYPE", 0, timer); throw new Exception("FAIL: timer inserted into datatype"); } catch (InvalidOperationException) { }
        if (StInstructionCatalog.All.Any(s => s.ReferenceOnly || s.Code.Contains("待接入")) || StInstructionCatalog.All.Select(s => s.Id).Distinct().Count() != StInstructionCatalog.All.Count) throw new Exception("FAIL: legacy motion placeholders must not remain in catalog");
        var namedTimer=timer with { InstanceNameHint="Delay" };
        var readable=StSnippetInsertion.Insert(source,source.IndexOf("END_PROGRAM",StringComparison.Ordinal),namedTimer);
        if(readable.InstancePrefix!="Delay_1")throw new Exception("FAIL: named instance prefix");
        var collisionSource=source.Replace("Existing : BOOL;","delay_1Input : BOOL;",StringComparison.Ordinal);
        var collision=StSnippetInsertion.Insert(collisionSource,collisionSource.IndexOf("END_PROGRAM",StringComparison.Ordinal),namedTimer);
        if(collision.InstancePrefix!="Delay_2")throw new Exception("FAIL: named helper collision must be case-insensitive");
        var reserved=StSnippetInsertion.Insert(source,source.IndexOf("END_PROGRAM",StringComparison.Ordinal),namedTimer,["DELAY_1"]);
        if(reserved.InstancePrefix!="Delay_2")throw new Exception("FAIL: reserved named instance collision");
        try { StSnippetInsertion.Insert(source,source.IndexOf("END_PROGRAM",StringComparison.Ordinal),timer with { InstanceNameHint="Bad-Name" });throw new Exception("FAIL: invalid name hint accepted"); } catch(InvalidOperationException) { }
        Console.WriteLine("PASS instruction declaration, uniqueness, lexical POU targeting, declaration redirection, datatype rejection and no legacy placeholders");
    }
}

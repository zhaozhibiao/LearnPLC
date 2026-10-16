namespace StWorkbench.Core;
public sealed record StSymbol(string Name, string Type, string Kind, string File, int Line, int Column, string? Owner = null);
public sealed record StMonitorContext(string Owner, string Path)
{
    public override string ToString() => $"{Path} · {Owner}";
}
public sealed record StMonitorReference(int EndOffset, string Path);
public sealed class StLanguageService
{
    public static readonly string[] Keywords = ["PROGRAM", "END_PROGRAM", "FUNCTION", "END_FUNCTION", "FUNCTION_BLOCK", "END_FUNCTION_BLOCK", "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_GLOBAL", "VAR_EXTERNAL", "END_VAR", "TYPE", "END_TYPE", "STRUCT", "END_STRUCT", "IF", "THEN", "ELSIF", "ELSE", "END_IF", "CASE", "OF", "END_CASE", "FOR", "TO", "BY", "DO", "END_FOR", "WHILE", "END_WHILE", "REPEAT", "UNTIL", "END_REPEAT", "RETURN", "TRUE", "FALSE", "AND", "OR", "NOT", "XOR", "MOD", "BOOL", "BYTE", "WORD", "DWORD", "INT", "DINT", "LINT", "UINT", "UDINT", "REAL", "LREAL", "STRING", "TIME", "ARRAY"];
    public string RenameLocalIdentifier(string source, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || !System.Text.RegularExpressions.Regex.IsMatch(newName, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new ArgumentException("无效的标识符");
        var tokens = Scan(source).Where(t => t.Text.Equals(oldName, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.Offset);
        var result = new System.Text.StringBuilder(source);
        foreach (var token in tokens) { result.Remove(token.Offset, token.Text.Length); result.Insert(token.Offset, newName); }
        return result.ToString();
    }
    public IReadOnlyList<StSymbol> Index(IReadOnlyList<SourceDocument> docs)
    {
        var result = new List<StSymbol>();
        foreach (var doc in docs)
        {
            var tokens = Scan(doc.Text); string? owner = null; string? varKind = null; bool typeBlock = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i]; var word = t.Text.ToUpperInvariant();
                if (word is "PROGRAM" or "FUNCTION" or "FUNCTION_BLOCK" or "TYPE")
                {
                    if (word == "TYPE") typeBlock = true;
                    if (i + 1 < tokens.Count && IsName(tokens[i + 1].Text))
                    {
                        var name = tokens[++i]; owner = name.Text;
                        var returnType = word is "FUNCTION" or "TYPE" && i + 2 < tokens.Count && tokens[i + 1].Text == ":" ? ReadType(tokens, i + 2) : word;
                        result.Add(new(name.Text, returnType, word, doc.RelativePath, name.Line, name.Column));
                    }
                    continue;
                }
                if (word.StartsWith("END_") && word != "END_VAR" && word != "END_STRUCT") { if (word is "END_PROGRAM" or "END_FUNCTION" or "END_FUNCTION_BLOCK" or "END_TYPE") owner = null; if (word == "END_TYPE") typeBlock = false; continue; }
                if (word is "VAR" or "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT" or "VAR_GLOBAL" or "VAR_EXTERNAL" or "STRUCT") { varKind = word; continue; }
                if (word is "END_VAR" or "END_STRUCT") { varKind = null; continue; }
                if (typeBlock && varKind is null && IsName(t.Text) && i > 0 && tokens[i - 1].Text == ";" && i + 2 < tokens.Count && tokens[i + 1].Text == ":")
                {
                    owner = t.Text;
                    result.Add(new(t.Text, ReadType(tokens, i + 2), "TYPE", doc.RelativePath, t.Line, t.Column));
                    continue;
                }
                if (varKind is null || !IsName(t.Text)) continue;
                // Recognize declaration heads only, preserving comma-separated identifiers.
                if (i > 0 && tokens[i - 1].Text != ";" && tokens[i - 1].Text.ToUpperInvariant() != varKind && tokens[i - 1].Text.ToUpperInvariant() is not "CONSTANT" and not "RETAIN" and not "NON_RETAIN" and not "PERSISTENT") continue;
                var names = new List<Token> { t }; int cursor = i + 1;
                while (cursor + 1 < tokens.Count && tokens[cursor].Text == "," && IsName(tokens[cursor + 1].Text)) { names.Add(tokens[cursor + 1]); cursor += 2; }
                if (cursor < tokens.Count && tokens[cursor].Text.Equals("AT", StringComparison.OrdinalIgnoreCase))
                    while (cursor < tokens.Count && tokens[cursor].Text != ":" && tokens[cursor].Text != ";" && !tokens[cursor].Text.Equals("END_VAR", StringComparison.OrdinalIgnoreCase)) cursor++;
                if (cursor + 1 >= tokens.Count || tokens[cursor].Text != ":") continue;
                var type = ReadType(tokens, cursor + 1);
                foreach (var name in names) result.Add(new(name.Text, type, varKind, doc.RelativePath, name.Line, name.Column, owner));
            }
        }
        return result.AsReadOnly();
    }
    public IReadOnlyList<StSymbol> Complete(IReadOnlyList<StSymbol> symbols, string prefix) => symbols.Concat(Keywords.Select(k => new StSymbol(k, "", "关键词", "", 0, 0)))
        .Where(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).OrderBy(s => s.Name).Take(100).ToArray();
    public string? Signature(IReadOnlyList<StSymbol> symbols, string name)
    {
        var symbol = symbols.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && s.Kind is "FUNCTION" or "FUNCTION_BLOCK");
        if (symbol is null)
        {
            var instance = symbols.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && s.Kind.StartsWith("VAR", StringComparison.Ordinal));
            if (instance is not null) symbol = symbols.FirstOrDefault(s => s.Name.Equals(instance.Type, StringComparison.OrdinalIgnoreCase) && s.Kind == "FUNCTION_BLOCK");
        }
        if (symbol is null) return null;
        var args = symbols.Where(s => s.Owner == symbol.Name && s.Kind is "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT").Select(s => $"{s.Name} : {s.Type} ({s.Kind.Replace("VAR_", "")})");
        return symbol.Name + "(" + string.Join("; ", args) + ")" + (symbol.Kind == "FUNCTION" ? " : " + symbol.Type : "");
    }
    private static bool IsName(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_');
    private static string ReadType(List<Token> tokens, int start)
    {
        // Lexical display only: retain ARRAY bounds/element type, never type-check them.
        var end = start + 1;
        if (tokens[start].Text.Equals("ARRAY", StringComparison.OrdinalIgnoreCase))
            while (end < tokens.Count && tokens[end].Text != ";" && tokens[end].Text != ":" && !tokens[end].Text.StartsWith("END_", StringComparison.OrdinalIgnoreCase)) end++;
        return string.Join(" ", tokens.Skip(start).Take(end - start).Select(t => t.Text))
            .Replace(". .", "..").Replace(" .. ", "..").Replace("[ ", "[").Replace(" ]", "]").Replace(" ,", ",");
    }
    public IReadOnlyList<StMonitorContext> MonitorContexts(IReadOnlyList<SourceDocument> docs, string program, IReadOnlyList<string> paths)
    {
        var symbols = Index(docs);
        var result = new List<StMonitorContext>();
        // This compiler emits one cyclic program as INSTANCE0. Require its actual debug prefix.
        if (!paths.Any(p => p.StartsWith("INSTANCE0.", StringComparison.OrdinalIgnoreCase))) return result;
        var pending = new Queue<(StMonitorContext Context, int Depth)>();
        pending.Enqueue((new(program, "INSTANCE0"), 0));
        while (pending.TryDequeue(out var item))
        {
            result.Add(item.Context);
            if (item.Depth >= 32 || result.Count >= 256) break;
            foreach (var variable in symbols.Where(s => s.Owner?.Equals(item.Context.Owner, StringComparison.OrdinalIgnoreCase) == true))
            {
                var block = symbols.FirstOrDefault(s => s.Kind == "FUNCTION_BLOCK" && s.Name.Equals(variable.Type, StringComparison.OrdinalIgnoreCase));
                var prefix = item.Context.Path + "." + variable.Name.ToUpperInvariant();
                if (block is not null && paths.Any(p => p.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))) pending.Enqueue((new(block.Name, prefix), item.Depth + 1));
            }
        }
        return result;
    }
    public IReadOnlyList<StMonitorReference> MonitorReferences(SourceDocument doc, StMonitorContext context, IReadOnlyList<string> paths)
    {
        var known = paths.ToDictionary(p => p, StringComparer.OrdinalIgnoreCase);
        var locals = Index([doc]).Where(s => s.Owner?.Equals(context.Owner, StringComparison.OrdinalIgnoreCase) == true && s.Kind.StartsWith("VAR", StringComparison.Ordinal)).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokens = Scan(doc.Text); var result = new List<StMonitorReference>(); string? owner = null; int parentheses = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i]; var word = t.Text.ToUpperInvariant();
            if (word is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION" or "TYPE") { owner = i + 1 < tokens.Count ? tokens[++i].Text : null; parentheses = 0; continue; }
            if (word is "END_PROGRAM" or "END_FUNCTION_BLOCK" or "END_FUNCTION" or "END_TYPE") { owner = null; continue; }
            if (t.Text == "(") { parentheses++; continue; }
            if (t.Text == ")") { parentheses = Math.Max(0, parentheses - 1); continue; }
            if (!string.Equals(owner, context.Owner, StringComparison.OrdinalIgnoreCase) || !locals.Contains(t.Text) || (i > 0 && tokens[i - 1].Text == ".")) continue;
            if (parentheses > 0 && i + 2 < tokens.Count && (tokens[i + 1].Text == ":" && tokens[i + 2].Text == "=" || tokens[i + 1].Text == "=" && tokens[i + 2].Text == ">")) continue;
            var path = context.Path + "." + t.Text; var end = t.Offset + t.Text.Length; int cursor = i;
            while (cursor + 2 < tokens.Count)
            {
                if (tokens[cursor + 1].Text == "." && IsName(tokens[cursor + 2].Text)) { path += "." + tokens[cursor + 2].Text; cursor += 2; end = tokens[cursor].Offset + tokens[cursor].Text.Length; }
                else if (cursor + 3 < tokens.Count && tokens[cursor + 1].Text == "[" && int.TryParse(tokens[cursor + 2].Text, out _) && tokens[cursor + 3].Text == "]") { path += "[" + tokens[cursor + 2].Text + "]"; cursor += 3; end = tokens[cursor].Offset + 1; }
                else break;
            }
            if (known.TryGetValue(path, out var actual)) result.Add(new(end, actual));
            i = cursor;
        }
        return result;
    }
    internal sealed record Token(string Text, int Line, int Column, int Offset);
    internal static List<Token> Scan(string text, List<(int Start, int End)>? protectedRanges = null, bool includeStrings = false)
    {
        var result = new List<Token>(); int offset = 0, line = 1, column = 1;
        void Advance() { if (text[offset++] == '\n') { line++; column = 1; } else column++; }
        while (offset < text.Length)
        {
            if (char.IsWhiteSpace(text[offset])) { Advance(); continue; }
            if (offset + 1 < text.Length && text[offset] == '/' && text[offset + 1] == '/') { int commentStart = offset; while (offset < text.Length && text[offset] != '\n') Advance(); protectedRanges?.Add((commentStart, offset)); continue; }
            if (offset + 1 < text.Length && text[offset] == '(' && text[offset + 1] == '*')
            {
                int commentStart = offset; int depth = 1; Advance(); Advance();
                while (offset < text.Length && depth > 0)
                {
                    if (offset + 1 < text.Length && text[offset] == '(' && text[offset + 1] == '*') { depth++; Advance(); Advance(); }
                    else if (offset + 1 < text.Length && text[offset] == '*' && text[offset + 1] == ')') { depth--; Advance(); Advance(); }
                    else Advance();
                }
                protectedRanges?.Add((commentStart, offset)); continue;
            }
            if (text[offset] is '\'' or '"')
            {
                int stringStart = offset; char quote = text[offset]; Advance();
                while (offset < text.Length)
                {
                    if (text[offset] == '$' && offset + 1 < text.Length) { Advance(); Advance(); continue; }
                    if (text[offset] == quote) { Advance(); if (offset < text.Length && text[offset] == quote) { Advance(); continue; } break; }
                    Advance();
                }
                protectedRanges?.Add((stringStart, offset)); if (includeStrings) result.Add(new(text[stringStart..offset], line, column, stringStart)); continue;
            }
            int start = offset, tokenLine = line, tokenColumn = column;
            if (char.IsLetter(text[offset]) || text[offset] == '_') { while (offset < text.Length && (char.IsLetterOrDigit(text[offset]) || text[offset] == '_')) Advance(); }
            else if (char.IsDigit(text[offset])) { while (offset < text.Length && char.IsDigit(text[offset])) Advance(); }
            else Advance();
            result.Add(new(text[start..offset], tokenLine, tokenColumn, start));
        }
        return result;
    }
}

namespace StWorkbench.Core;

public sealed record StInsertionResult(string Text, int SelectionStart, int SelectionLength, string InstancePrefix);

public static class StSnippetInsertion
{
    public static StInsertionResult Insert(string source, int offset, StInstruction instruction, IEnumerable<string>? reservedNames = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var protectedRanges = new List<(int Start, int End)>();
        var tokens = StLanguageService.Scan(source, protectedRanges);
        if (protectedRanges.Any(r => offset > r.Start && offset < r.End)) throw new InvalidOperationException("请拖到完整代码行上；注释或字符串内部不能插入执行指令。");
        int start = -1, end = -1; string kind = "";
        for (int i = 0; i < tokens.Count; i++)
        {
            var word = tokens[i].Text.ToUpperInvariant();
            if (word is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION") { start = i; kind = word; }
            if (start >= 0 && word == "END_" + kind)
            {
                if (offset >= tokens[start].Offset && offset <= tokens[i].Offset + tokens[i].Text.Length) { end = i; break; }
                start = -1;
            }
        }
        if (start < 0 || end < 0) throw new InvalidOperationException("请在程序、函数或功能块的代码段中插入指令；数据类型与全局变量页不接受执行指令。");
        if (kind == "FUNCTION" && (instruction.Stateful || instruction.Declarations.Any(d => d.EndsWith(" : " + instruction.Id + ";", StringComparison.Ordinal)))) throw new InvalidOperationException("有状态指令请放在程序或功能块中；函数不保存计时器和计数器实例。");
        var names = tokens.Select(t => t.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (reservedNames is not null) names.UnionWith(reservedNames);
        string prefix; int serial = 0;
        if(instruction.InstanceNameHint is { } hint && !System.Text.RegularExpressions.Regex.IsMatch(hint,@"^[A-Za-z_][A-Za-z0-9_]*\z"))throw new InvalidOperationException("实例名称提示必须是有效 ST 标识符。");
        do { prefix = instruction.InstanceNameHint is { } name ? name + "_" + (++serial) : "Snippet" + instruction.Id + (++serial); }
        while (instruction.Declarations.Select(d => d.Replace("{p}", prefix).Split(':')[0].Trim()).Any(names.Contains));
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var headerEnd = source.IndexOf('\n', tokens[start].Offset);
        if (headerEnd < 0 || headerEnd >= tokens[end].Offset) throw new InvalidOperationException("请先将 POU 声明与代码分行，再插入模板。");
        int bodyStart = headerEnd + 1, declarationAt = -1; bool localVar = false;
        for (int i = start + 1; i < end; i++)
        {
            var word = tokens[i].Text.ToUpperInvariant();
            if (word.StartsWith("VAR", StringComparison.Ordinal)) localVar = word == "VAR";
            if (word == "END_VAR")
            {
                if (localVar) declarationAt = tokens[i].Offset;
                var nextLine = source.IndexOf('\n', tokens[i].Offset + tokens[i].Text.Length);
                if (nextLine < 0 || nextLine > tokens[end].Offset) throw new InvalidOperationException("请将 END_VAR 与执行代码分行后再插入模板。");
                bodyStart = nextLine + 1; localVar = false;
            }
        }
        offset = Math.Clamp(offset, bodyStart, tokens[end].Offset);
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        if (lineStart < bodyStart) throw new InvalidOperationException("请将声明与执行代码分行后再插入模板。");
        if (protectedRanges.Any(r => lineStart > r.Start && lineStart < r.End)) throw new InvalidOperationException("这一行仍属于跨行注释或字符串，请拖到其结束后的独立代码行。");
        var prior = tokens.Take(end).LastOrDefault(t => t.Offset < lineStart);
        if (prior is not null && prior.Offset > headerEnd && prior.Text.ToUpperInvariant() is not ";" and not "END_VAR" and not "THEN" and not "DO" and not "ELSE" and not "REPEAT" and not ":")
            throw new InvalidOperationException("这里是跨行语句内部；请拖到一条完整语句的起始行或结束后的空行。");
        var lineEnd = source.IndexOf('\n', lineStart); if (lineEnd < 0) lineEnd = source.Length;
        var indent = new string(source[lineStart..lineEnd].TakeWhile(c => c is ' ' or '\t').ToArray());
        offset = lineStart;
        var code = string.Join(newline + indent, instruction.Code.Replace("{p}", prefix).Split('\n'));
        var leading = indent;
        var inserted = leading + code + newline;
        var text = source.Insert(offset, inserted); int selection = offset + leading.Length;
        if (instruction.Declarations.Length > 0)
        {
            var declarations = string.Join(newline, instruction.Declarations.Select(d => "    " + d.Replace("{p}", prefix))) + newline;
            if (declarationAt < 0) { declarationAt = headerEnd + 1; declarations = "VAR" + newline + declarations + "END_VAR" + newline; }
            else if (declarationAt > 0 && source[declarationAt - 1] != '\n') declarations = newline + declarations;
            text = text.Insert(declarationAt, declarations);
            if (declarationAt <= offset) selection += declarations.Length;
        }
        return new(text, selection, code.Length, prefix);
    }
}

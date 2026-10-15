namespace StWorkbench.Core;

public sealed record ProjectResourceReference(Guid ItemId, string? Member = null);

public static class StResourceInsertion
{
    public static StInsertionResult Insert(string source, int offset, ProjectItem resource, SourceDocument definition,
        IReadOnlyList<SourceDocument> projectSources, string? member = null, string? existingInstance = null)
    {
        if (resource.Id != definition.ItemId) throw new InvalidOperationException("资源与源文件身份不一致。");
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var ranges = new List<(int Start, int End)>();
        var tokens = StLanguageService.Scan(source, ranges);
        if (ranges.Any(r => offset > r.Start && offset < r.End)) throw new InvalidOperationException("不能插入注释或字符串内部。");
        var language = new StLanguageService();
        var symbols = language.Index([definition]);
        var reserved = projectSources.SelectMany(s => StLanguageService.Scan(s.Text)).Select(t => t.Text).ToArray();
        if (resource.Kind == ItemKind.GlobalVariables)
        {
            if (member is null || symbols.Count(s => s.Kind == "VAR_GLOBAL" && s.Name.Equals(member, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("请展开全局变量资源并选择一个有效变量。");
            var context = Context(tokens, offset);
            if (context.Owner is null || context.VariableKind is not null || offset < ExecutionStart(source, tokens, offset)) throw new InvalidOperationException("请将变量引用拖入执行代码或表达式。");
            if (language.Index([new(Guid.Empty, "target.st", source)]).Any(s => s.Owner == context.Owner && s.Kind.StartsWith("VAR", StringComparison.Ordinal) && s.Name.Equals(member, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("本地同名变量遮蔽此全局变量，请先消除重名。");
            if (tokens.Any(t => offset > t.Offset && offset < t.Offset + t.Text.Length) || offset > 0 && IsIdentifier(source[offset - 1]) || offset < source.Length && IsIdentifier(source[offset]))
                throw new InvalidOperationException("请将变量拖到表达式中的空白位置，避免拼接已有标识符。");
            return new(source.Insert(offset, member), offset, member.Length, member);
        }
        if (resource.Kind == ItemKind.DataType)
        {
            var name = member ?? resource.Name;
            if (symbols.Count(s => s.Kind == "TYPE" && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) != 1) throw new InvalidOperationException("数据类型不存在，请展开资源选择实际类型。");
            var context = Context(tokens, offset);
            if (context.VariableKind is not ("VAR" or "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT" or "VAR_GLOBAL")) throw new InvalidOperationException("数据类型请拖入变量声明区。");
            int start = source.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
            if (ranges.Any(r => start > r.Start && start < r.End)) throw new InvalidOperationException("不能插入跨行注释内部。");
            var previous = tokens.LastOrDefault(t => t.Offset < start);
            if (previous is null || previous.Text != ";" && previous.Text.ToUpperInvariant() != context.VariableKind)
                throw new InvalidOperationException("请拖到完整声明行的起始位置或声明区空行。");
            var used = tokens.Select(t => t.Text).Concat(reserved).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int number = 1; while (used.Contains("My" + name + number)) number++;
            var variable = "My" + name + number;
            var end = source.IndexOf('\n', start); if (end < 0) end = source.Length;
            var indent = new string(source[start..end].TakeWhile(c => c is ' ' or '\t').ToArray());
            var line = indent + variable + " : " + name + ";" + (source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
            return new(source.Insert(start, line), start + indent.Length, line.TrimEnd('\r', '\n').Length - indent.Length, variable);
        }
        if (resource.Kind is not (ItemKind.Function or ItemKind.FunctionBlock)) throw new InvalidOperationException("程序由任务调用，不能作为函数拖入。");
        var kind = resource.Kind == ItemKind.Function ? "FUNCTION" : "FUNCTION_BLOCK";
        var head = symbols.SingleOrDefault(s => s.Kind == kind && s.Name.Equals(resource.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("资源名称与声明接口不一致，请检查源文件。");
        var parameters = Parameters(definition.Text, head.Name);
        var declarations = new List<string>();
        var arguments = new List<string>();
        foreach (var parameter in parameters)
        {
            if (resource.Kind == ItemKind.FunctionBlock && parameter.Kind == "VAR_OUTPUT") continue;
            declarations.Add("{p}_" + parameter.Name + " : " + parameter.Type + ";");
            arguments.Add(parameter.Name + (parameter.Kind == "VAR_OUTPUT" ? " => " : " := ") + "{p}_" + parameter.Name);
        }
        string code;
        if (resource.Kind == ItemKind.Function)
        {
            var returnType = FunctionReturnType(definition.Text, head.Name);
            var resultName = "Result"; int resultSerial = 0;
            while (parameters.Any(p => p.Name.Equals(resultName, StringComparison.OrdinalIgnoreCase))) resultName = "Result" + (++resultSerial);
            declarations.Add("{p}_" + resultName + " : " + returnType + ";");
            code = "{p}_" + resultName + " := " + head.Name + "(" + string.Join(", ", arguments) + ");";
        }
        else
        {
            var instance = "{p}";
            if (existingInstance is not null)
            {
                var owner = Context(tokens, offset).Owner;
                if (!language.Index([new(Guid.Empty, "target.st", source)]).Any(s => s.Owner == owner && s.Kind == "VAR" && s.Type.Equals(head.Name, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(existingInstance, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("选择的实例不是当前程序的同类型本地实例。");
                instance = existingInstance;
            }
            else declarations.Insert(0, "{p} : " + head.Name + ";");
            code = instance + "(" + string.Join(", ", arguments) + ");";
            var outputs = parameters.Where(s => s.Kind == "VAR_OUTPUT").Select(s => instance + "." + s.Name).ToArray();
            if (outputs.Length > 0) code += "\n(* 输出：" + string.Join(", ", outputs) + " *)";
        }
        var instruction = new StInstruction(head.Name, head.Name, "工程资源", "请修改参数占位变量为实际信号。", code, declarations.ToArray(), Stateful: resource.Kind == ItemKind.FunctionBlock);
        return StSnippetInsertion.Insert(source, offset, instruction, reserved);
    }

    public static IReadOnlyList<string> ExistingInstances(string source, int offset, string blockType)
    {
        var owner = Context(StLanguageService.Scan(source), offset).Owner;
        return new StLanguageService().Index([new(Guid.Empty, "target.st", source)])
            .Where(s => s.Owner == owner && s.Kind == "VAR" && s.Type.Equals(blockType, StringComparison.OrdinalIgnoreCase)).Select(s => s.Name).ToArray();
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static int ExecutionStart(string source, List<StLanguageService.Token> tokens, int offset)
    {
        int start = tokens.FindLastIndex(t => t.Offset < offset && t.Text.ToUpperInvariant() is "PROGRAM" or "FUNCTION" or "FUNCTION_BLOCK");
        if (start < 0) return int.MaxValue;
        var endKind = "END_" + tokens[start].Text.ToUpperInvariant();
        int end = tokens.FindIndex(start + 1, t => t.Text.Equals(endKind, StringComparison.OrdinalIgnoreCase));
        if (end < 0) return int.MaxValue;
        int body = source.IndexOf('\n', tokens[start].Offset) + 1;
        if (body == 0) return int.MaxValue;
        for (int i = start + 1; i < end; i++)
            if (tokens[i].Text.Equals("END_VAR", StringComparison.OrdinalIgnoreCase)) body = source.IndexOf('\n', tokens[i].Offset + tokens[i].Text.Length) + 1;
        return body == 0 ? int.MaxValue : body;
    }
    private static (string? Owner, string? VariableKind) Context(List<StLanguageService.Token> tokens, int offset)
    {
        string? owner = null, variable = null;
        for (int i = 0; i < tokens.Count && tokens[i].Offset < offset; i++)
        {
            var word = tokens[i].Text.ToUpperInvariant();
            if (word is "PROGRAM" or "FUNCTION" or "FUNCTION_BLOCK") { owner = i + 1 < tokens.Count ? tokens[++i].Text : null; variable = null; }
            else if (word is "END_PROGRAM" or "END_FUNCTION" or "END_FUNCTION_BLOCK") { owner = null; variable = null; }
            else if (word is "VAR" or "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT" or "VAR_GLOBAL" or "VAR_EXTERNAL") variable = word;
            else if (word == "END_VAR") variable = null;
        }
        return (owner, variable);
    }

    private static IReadOnlyList<StSymbol> Parameters(string source, string owner)
    {
        var symbols = new StLanguageService().Index([new(Guid.Empty, "definition.st", source)])
            .Where(s => s.Owner == owner && s.Kind is "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT").ToArray();
        var tokens = StLanguageService.Scan(source);
        return symbols.Select(s =>
        {
            int at = tokens.FindIndex(t => t.Line == s.Line && t.Column == s.Column);
            int colon = at + 1;
            while (colon < tokens.Count && tokens[colon].Text != ":" && tokens[colon].Text != ";") colon++;
            if (colon >= tokens.Count || tokens[colon].Text != ":") throw new InvalidOperationException("无法读取参数类型：" + s.Name);
            int end = colon + 1;
            while (end < tokens.Count && tokens[end].Text != ";" && !(tokens[end].Text == ":" && end + 1 < tokens.Count && tokens[end + 1].Text == "=")) end++;
            if (end >= tokens.Count || end == colon + 1) throw new InvalidOperationException("参数声明不完整：" + s.Name);
            return s with { Type = TypeText(tokens.Skip(colon + 1).Take(end - colon - 1)) };
        }).ToArray();
    }
    private static string FunctionReturnType(string source, string name)
    {
        var tokens = StLanguageService.Scan(source);
        int at = tokens.FindIndex(t => t.Text.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 3 >= tokens.Count || tokens[at + 1].Text != name || tokens[at + 2].Text != ":") throw new InvalidOperationException("函数返回类型不完整。");
        int end = TypeEnd(tokens, at + 3);
        return TypeText(tokens.Skip(at + 3).Take(end - at - 3));
    }
    private static int TypeEnd(List<StLanguageService.Token> tokens, int start, int depth = 0)
    {
        if (depth > 32 || start >= tokens.Count || !IsIdentifier(tokens[start].Text[0]) || IsTypeBoundary(tokens[start].Text)) throw new InvalidOperationException("函数返回类型不完整。");
        var root = tokens[start].Text.ToUpperInvariant();
        int end = start + 1;
        if (root == "REF_TO") return TypeEnd(tokens, end, depth + 1);
        if (root == "POINTER")
        {
            if (end >= tokens.Count || !tokens[end].Text.Equals("TO", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("指针类型缺少 TO。");
            return TypeEnd(tokens, end + 1, depth + 1);
        }
        if (end < tokens.Count && tokens[end].Text is "[" or "(")
        {
            var closing = tokens[end].Text == "[" ? "]" : ")"; int nesting = 1; var opening = tokens[end++].Text;
            while (end < tokens.Count && nesting > 0)
            {
                if (IsTypeBoundary(tokens[end].Text) || tokens[end].Text == ";") throw new InvalidOperationException("类型范围未闭合。");
                if (tokens[end].Text == opening) nesting++;
                if (tokens[end].Text == closing) nesting--;
                end++;
            }
            if (nesting != 0) throw new InvalidOperationException("类型范围未闭合。");
        }
        if (root == "ARRAY")
        {
            if (end >= tokens.Count || !tokens[end].Text.Equals("OF", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("数组返回类型缺少 OF。");
            return TypeEnd(tokens, end + 1, depth + 1);
        }
        return end;
    }
    private static bool IsTypeBoundary(string word) => word.ToUpperInvariant() is "VAR" or "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT" or "VAR_GLOBAL" or "VAR_EXTERNAL" or "END_VAR" or "END_FUNCTION" or "END_FUNCTION_BLOCK" or "END_PROGRAM" or "END_TYPE" or "END_STRUCT";
    private static string TypeText(IEnumerable<StLanguageService.Token> tokens)
    {
        var values = tokens.ToArray();
        var text = string.Join(" ", values.Select(t => t.Text)).Replace(". .", "..", StringComparison.Ordinal)
            .Replace("[ ", "[", StringComparison.Ordinal).Replace(" ]", "]", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(text) || values.Any(t => IsTypeBoundary(t.Text) || t.Text.Equals("STRUCT", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("无法安全读取完整类型声明，请将内嵌结构定义为独立数据类型。");
        return text;
    }
}

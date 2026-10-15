namespace StWorkbench.Core;

public static class AxisControlBinding
{
    public static readonly string[] BlockNames = ["MC_Power", "MC_Reset", "MC_Home", "MC_MoveAbsolute", "MC_MoveVelocity", "MC_Stop", "MC_ReadAxisStatus", "MC_ReadActualPosition", "MC_ReadActualVelocity"];
    public static bool UsesAxisControl(IReadOnlyList<SourceDocument> sources) => sources.SelectMany(s => StLanguageService.Scan(s.Text)).Any(t => BlockNames.Contains(t.Text, StringComparer.OrdinalIgnoreCase) || t.Text.Equals("AXIS_REF", StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<Diagnostic> Validate(IReadOnlyList<SourceDocument> sources, IReadOnlyList<AxisConfiguration> axes, IReadOnlyList<TaskSnapshot> tasks)
    {
        var errors = new List<Diagnostic>();
        if (!UsesAxisControl(sources)) return errors;
        void Error(string message, string file = "", int line = 0, int column = 0) => errors.Add(new(file, line, column, "error", message));
        if (axes.Count is < 1 or > 64) Error("基础轴控需要配置 1–64 个工程轴。");
        if (tasks.Count(t => t.Enabled) != 1 || tasks.Any(t => t.Enabled && t.IntervalMilliseconds is < 1 or > 1000)) Error("基础轴控程序的 PLC 周期必须为 1–1000 ms，以便有界扫描看门狗检测。");
        var constants = axes.Select(a => "AXIS_" + a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var symbols = new StLanguageService().Index(sources);
        var aliases = symbols.Where(s => s.Kind == "TYPE").GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Type, StringComparer.OrdinalIgnoreCase);
        string Resolve(string type)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (aliases.TryGetValue(type, out var next) && seen.Add(type)) type = next;
            return type;
        }
        bool IsAxis(string type) => BlockNames.Contains(Resolve(type), StringComparer.OrdinalIgnoreCase);
        var functions = symbols.Where(s => s.Kind == "FUNCTION").Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containers = symbols.Where(s => s.Kind is "FUNCTION_BLOCK" or "TYPE").Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statefulAxisTypes = BlockNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool ContainsAxisState(string type) => statefulAxisTypes.Contains(type) || statefulAxisTypes.Contains(Resolve(type)) ||
            statefulAxisTypes.Any(name => System.Text.RegularExpressions.Regex.IsMatch(Resolve(type), @"\b" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        // A local wrapper FB in a FUNCTION also reinitializes its nested axis
        // instances on each call. Compute the transitive containment closure.
        bool changed;
        do
        {
            changed = false;
            foreach (var member in symbols.Where(s => s.Owner is not null && containers.Contains(s.Owner) && (s.Kind.StartsWith("VAR", StringComparison.Ordinal) || s.Kind == "STRUCT")))
                if (ContainsAxisState(member.Type)) changed |= statefulAxisTypes.Add(member.Owner!);
            foreach (var alias in aliases)
                if (ContainsAxisState(alias.Value)) changed |= statefulAxisTypes.Add(alias.Key);
        } while (changed);
        foreach (var symbol in symbols)
        {
            if (constants.Contains(symbol.Name)) Error("工程轴常量 " + symbol.Name + " 被用户声明遮蔽，请改名。", symbol.File, symbol.Line, symbol.Column);
            if (symbol.Owner is not null && functions.Contains(symbol.Owner) && ContainsAxisState(symbol.Type)) Error("轴控功能块需要保留跨扫描状态，请在程序或功能块中声明，不能放在函数局部变量中。", symbol.File, symbol.Line, symbol.Column);
            var type = Resolve(symbol.Type);
            if (type.StartsWith("ARRAY", StringComparison.OrdinalIgnoreCase) && symbols.Where(s => IsAxis(s.Name) || IsAxis(s.Type)).Select(s => s.Name).Concat(BlockNames).Any(n => System.Text.RegularExpressions.Regex.IsMatch(type, @"\b" + System.Text.RegularExpressions.Regex.Escape(n) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                Error("基础轴控实例数组尚未支持，请使用明确的标量实例。", symbol.File, symbol.Line, symbol.Column);
            if (symbol.Kind is "FUNCTION" or "FUNCTION_BLOCK" or "TYPE" && BlockNames.Contains(symbol.Name, StringComparer.OrdinalIgnoreCase)) Error("基础轴控库类型名称不能由用户重新定义。", symbol.File, symbol.Line, symbol.Column);
        }
        foreach (var source in sources)
        {
            var tokens = StLanguageService.Scan(source.Text); string? owner = null;
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i]; var word = token.Text.ToUpperInvariant();
                if (word.StartsWith("AB_", StringComparison.Ordinal)) { Error("AB_ 为轴控扫描桥保留名称，不能直接读写或调用。", source.RelativePath, token.Line, token.Column); continue; }
                if (word is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION") { owner = i + 1 < tokens.Count ? tokens[++i].Text : null; continue; }
                if (word is "END_PROGRAM" or "END_FUNCTION_BLOCK" or "END_FUNCTION") { owner = null; continue; }
                if (owner is null || i + 1 >= tokens.Count || tokens[i + 1].Text != "(") continue;
                var variable = symbols.FirstOrDefault(s => s.Owner?.Equals(owner, StringComparison.OrdinalIgnoreCase) == true && s.Name.Equals(token.Text, StringComparison.OrdinalIgnoreCase) && s.Kind.StartsWith("VAR", StringComparison.Ordinal))
                    ?? symbols.FirstOrDefault(s => s.Kind == "VAR_GLOBAL" && s.Name.Equals(token.Text, StringComparison.OrdinalIgnoreCase));
                if (variable is null || !IsAxis(variable.Type))
                {
                    if (i > 0 && tokens[i - 1].Text == "." && symbols.Any(s => s.Name.Equals(token.Text, StringComparison.OrdinalIgnoreCase) && IsAxis(s.Type))) Error("嵌套轴控实例请在其声明功能块中显式绑定工程轴。", source.RelativePath, token.Line, token.Column);
                    continue;
                }
                int depth = 1, end = i + 2;
                var axisArguments = new List<string[]>();
                for (; end < tokens.Count && depth > 0; end++)
                {
                    if (tokens[end].Text == "(") { depth++; continue; }
                    if (tokens[end].Text == ")") { depth--; continue; }
                    if (depth != 1 || !tokens[end].Text.Equals("Axis", StringComparison.OrdinalIgnoreCase) || end + 2 >= tokens.Count || tokens[end + 1].Text != ":" || tokens[end + 2].Text != "=") continue;
                    int at = end + 3;
                    while (at < tokens.Count && tokens[at].Text is not "," and not ")") at++;
                    axisArguments.Add(tokens.Skip(end + 3).Take(at - end - 3).Select(t => t.Text).ToArray());
                }
                if (depth != 0 || axisArguments.Count != 1 || axisArguments[0].Length != 1 || !constants.Contains(axisArguments[0][0]))
                    Error("轴控调用的 Axis 必须显式绑定工程轴常量（例如 AXIS_X），不接受未知或动态引用。", source.RelativePath, token.Line, token.Column);
            }
        }
        return errors;
    }
    public static string Constants(IReadOnlyList<AxisConfiguration> axes) => "VAR_GLOBAL CONSTANT\n" + string.Join("\n", axes.OrderBy(a => a.Name, StringComparer.Ordinal).Select((a, i) => "    AXIS_" + a.Name + " : AXIS_REF := " + (i + 1) + ";")) + "\nEND_VAR\n";
}

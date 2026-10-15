using System.Globalization;

namespace StWorkbench.Core;

public static class PathBindingValidator
{
    public static IReadOnlyList<Diagnostic> Validate(IReadOnlyList<SourceDocument> sources, IReadOnlyList<MotionPathAsset> paths, ICollection<MotionPathAsset>? referencedPaths = null)
    {
        var symbols = new StLanguageService().Index(sources); var errors = new List<Diagnostic>();
        var motionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MC_ExecuteContinuousPath" };
        var pending = new Queue<string>(); pending.Enqueue("MC_ExecuteContinuousPath");
        var aliases = symbols.Where(s => s.Kind == "TYPE").ToLookup(s => s.Type, StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var type)) foreach (var alias in aliases[type]) if (motionTypes.Add(alias.Name)) pending.Enqueue(alias.Name);
        var containers = new HashSet<string>(motionTypes, StringComparer.OrdinalIgnoreCase);
        var definitions = symbols.Where(s => s.Kind is "TYPE" or "FUNCTION_BLOCK").Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parents = symbols.Where(s => s.Owner is not null && definitions.Contains(s.Owner)).ToLookup(s => s.Type, StringComparer.OrdinalIgnoreCase);
        foreach (var type in motionTypes) pending.Enqueue(type);
        while (pending.TryDequeue(out var type)) foreach (var field in parents[type]) if (containers.Add(field.Owner!)) pending.Enqueue(field.Owner!);
        foreach (var symbol in symbols.Where(s => s.Type.StartsWith("ARRAY", StringComparison.OrdinalIgnoreCase) && containers.Any(type => System.Text.RegularExpressions.Regex.IsMatch(s.Type, "\\b" + System.Text.RegularExpressions.Regex.Escape(type) + "\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))))
            errors.Add(new(symbol.File, symbol.Line, symbol.Column, "error", "连续路径功能块数组当前不接受；请使用资源库生成独立且明确绑定版本的实例。"));
        foreach (var source in sources)
        {
            var tokens = StLanguageService.Scan(source.Text, includeStrings: true); string? owner = null;
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i]; var word = token.Text.ToUpperInvariant();
                if (word is "PROGRAM" or "FUNCTION" or "FUNCTION_BLOCK" or "TYPE") { owner = i + 1 < tokens.Count ? tokens[++i].Text : null; continue; }
                if (word is "END_PROGRAM" or "END_FUNCTION" or "END_FUNCTION_BLOCK" or "END_TYPE") { owner = null; continue; }
                if (owner is null || i + 1 >= tokens.Count || tokens[i + 1].Text != "(") continue;
                int root = i; while (root >= 2 && tokens[root - 1].Text == ".") root -= 2;
                var variable = symbols.FirstOrDefault(s => s.File == source.RelativePath && string.Equals(s.Owner, owner, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(tokens[root].Text, StringComparison.OrdinalIgnoreCase))
                    ?? symbols.FirstOrDefault(s => s.Kind == "VAR_GLOBAL" && s.Name.Equals(tokens[root].Text, StringComparison.OrdinalIgnoreCase));
                for (int member = root + 2; variable is not null && member <= i; member += 2)
                {
                    var type = variable.Type; variable = symbols.FirstOrDefault(s => string.Equals(s.Owner, type, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(tokens[member].Text, StringComparison.OrdinalIgnoreCase));
                }
                if (variable is null || !motionTypes.Contains(variable.Type)) continue;
                var arguments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); int depth = 1, cursor = i + 2; List<string>? value = null; bool malformed = false;
                for (; cursor < tokens.Count; cursor++)
                {
                    string text = tokens[cursor].Text;
                    if (text == "(") depth++;
                    if (text == ")" && --depth == 0) break;
                    if (text != ")" && depth < 0) break;
                    if (depth == 1 && cursor + 2 < tokens.Count && tokens[cursor + 1].Text == ":" && tokens[cursor + 2].Text == "=")
                    {
                        value = []; if (!arguments.TryAdd(text, value)) { malformed = true; break; } cursor += 2; continue;
                    }
                    if (depth == 1 && text == ",") { value = null; continue; }
                    value?.Add(text);
                }
                bool Numeric(string name, out ulong number)
                {
                    number = 0; if (!arguments.TryGetValue(name, out var argument)) return false;
                    var value = argument.Count == 3 && argument[1] == "#" && argument[0].ToUpperInvariant() is "ULINT" or "UDINT" ? argument[2] : argument.Count == 1 ? argument[0] : "";
                    return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
                }
                string? hash = arguments.TryGetValue("ContentHash", out var h) && h.Count == 1 && h[0].Length == 66 && h[0][0] == '\'' && h[0][^1] == '\'' ? h[0][1..^1] : null;
                if (malformed || depth != 0 || !Numeric("PathId", out var id) || !Numeric("Version", out var version) || hash is null)
                    errors.Add(new(source.RelativePath, token.Line, token.Column, "error", "连续路径调用必须绑定预存的字面量 ID、版本和完整摘要；请从工程路径资源库插入。动态路径引用当前不接受。"));
                else
                {
                    var referenced = paths.FirstOrDefault(p => p.NumericId == id && p.Version == version && p.ContentHash == hash);
                    if (referenced is null) errors.Add(new(source.RelativePath, token.Line, token.Column, "error", "ST 路径引用在工程预存资产中不存在或版本/摘要不一致。"));
                    else referencedPaths?.Add(referenced);
                }
                i = Math.Max(i, cursor);
            }
        }
        return errors;
    }
}

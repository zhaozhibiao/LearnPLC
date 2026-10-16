namespace StWorkbench.Core;

public static class ControlLibraryBinding
{
    public static readonly string[] Blocks = ["WB_PID", "WB_INCREMENTAL_PID", "WB_CASCADE_PID", "WB_ONOFF", "WB_LOW_PASS", "WB_MOVING_AVERAGE", "WB_MEDIAN", "WB_KALMAN_1D", "WB_SLEW_RATE", "WB_LEAD_LAG", "WB_INTEGRATOR", "WB_DERIVATIVE", "WB_SCALE", "WB_DEADBAND", "WB_TIME_PROPORTION"];
    public static bool IsUsed(IReadOnlyList<SourceDocument> sources) => sources.SelectMany(s => StLanguageService.Scan(s.Text)).Any(t => Blocks.Contains(t.Text, StringComparer.OrdinalIgnoreCase) || t.Text.Equals("WB_FINITE", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("WB_CLAMP", StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<Diagnostic> Validate(IReadOnlyList<SourceDocument> sources)
    {
        var symbols = new StLanguageService().Index(sources); var errors = new List<Diagnostic>();
        var containers = symbols.Where(s => s.Kind is "FUNCTION_BLOCK" or "TYPE").Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stateful = Blocks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool HasState(string type) => stateful.Any(n => System.Text.RegularExpressions.Regex.IsMatch(type, @"\b" + System.Text.RegularExpressions.Regex.Escape(n) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        bool changed;
        do { changed = false; foreach (var member in symbols.Where(s => s.Owner is not null && containers.Contains(s.Owner))) if (HasState(member.Type)) changed |= stateful.Add(member.Owner!); } while (changed);
        foreach (var s in symbols)
        {
            if (s.Kind is "FUNCTION" or "FUNCTION_BLOCK" or "TYPE" && (Blocks.Contains(s.Name, StringComparer.OrdinalIgnoreCase) || s.Name.Equals("WB_FINITE", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("WB_CLAMP", StringComparison.OrdinalIgnoreCase))) errors.Add(new(s.File, s.Line, s.Column, "error", "控制库名称不能被重新定义。"));
            if (s.Owner is { } owner && symbols.Any(f => f.Kind == "FUNCTION" && f.Name == owner) && HasState(s.Type)) errors.Add(new(s.File, s.Line, s.Column, "error", "控制算法功能块需要跨扫描状态，请在程序或功能块中声明。"));
        }
        return errors;
    }
}

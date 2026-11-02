namespace StWorkbench.Core;

public sealed record MotionCycleConfiguration
{
    public bool Enabled { get; init; }
    public int PeriodMicroseconds { get; init; } = 1000;
    public string SchedulingPolicy { get; init; } = "FIFO";
    public int Priority { get; init; } = 80;
    public int? Cpu { get; init; }
    public int WatchdogCycles { get; init; } = 3;
    private readonly string[] axisNames = [];
    public string[] AxisNames { get => axisNames?.ToArray()!; init => axisNames = value?.ToArray()!; }
}

public static class MotionCycleValidator
{
    public static IReadOnlyList<string> Validate(MotionCycleConfiguration? config, IReadOnlyList<AxisConfiguration> axes, IReadOnlyList<MotionPathAsset> paths)
    {
        var errors = new List<string>();
        if (config is null || !config.Enabled) return errors;
        if (config.PeriodMicroseconds is < 100 or > 100000) errors.Add("轴控周期必须为 100–100000 μs；具体驱动支持范围需由执行端核验。");
        if (config.SchedulingPolicy != "FIFO") errors.Add("轴控实时调度当前只接受 FIFO。");
        if (config.Priority is < 60 or > 89) errors.Add("轴控实时优先级必须为 60–89，确保高于 PLC 实时任务；旧工程请明确调整后重新下载配置。");
        if (config.Cpu is < 0 or > 1023) errors.Add("CPU 编号必须为 0–1023，或不绑定；目标设备还需核验核心是否存在。");
        if (config.WatchdogCycles is < 1 or > 100) errors.Add("连续周期超时阈值必须为 1–100 个周期。");
        if (config.AxisNames is null || config.AxisNames.Length is < 1 or > 64 || config.AxisNames.Any(string.IsNullOrWhiteSpace))
        { errors.Add("启用轴控周期必须选择 1–64 个现有工程轴。"); return errors; }
        var names = config.AxisNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count != config.AxisNames.Length) errors.Add("轴控周期的轴组存在重复轴。");
        var mapped = axes.Where(a => config.AxisNames.Contains(a.Name, StringComparer.Ordinal)).ToArray();
        if (mapped.Length != names.Count) errors.Add("轴控周期引用的轴不存在，请检查硬件与轴配置。");
        if (mapped.Select(a => a.ControllerAxis).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapped.Length) errors.Add("同一轴控周期不能重复映射同一个控制器轴。");
        if (mapped.Any(a => a.Name.Length > 256 || string.IsNullOrWhiteSpace(a.ControllerAxis) || a.ControllerAxis.Length > 256 || string.IsNullOrWhiteSpace(a.Unit) || a.Unit.Length > 256 || !double.IsFinite(a.MinPosition) || !double.IsFinite(a.MaxPosition) || a.MinPosition >= a.MaxPosition || (a.MaximumJerk is double jerk && (!double.IsFinite(jerk) || jerk < 0)) || new[] { a.MaximumVelocity, a.MaximumAcceleration, a.MaximumDeceleration }.Any(v => v is double n && (!double.IsFinite(n) || n <= 0))))
            errors.Add("轴控周期中的轴元数据或动态限制无效。");
        foreach (var path in paths)
        {
            if (path.Axes.Any(a => !config.AxisNames.Contains(a, StringComparer.Ordinal))) errors.Add("路径 " + path.Name + " 的轴不属于轴控周期的轴组。");
            if (!double.IsFinite(path.Profile.CycleSeconds) || Math.Abs(path.Profile.CycleSeconds * 1000000 - config.PeriodMicroseconds) > .01)
                errors.Add("路径 " + path.Name + " 的采样周期与轴控周期不一致，请生成匹配的新路径版本。");
        }
        return errors;
    }
}

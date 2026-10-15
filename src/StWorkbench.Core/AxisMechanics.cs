using System.Text.Json.Serialization;

namespace StWorkbench.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AxisKind>))]
public enum AxisKind { Linear, Rotary }
[JsonConverter(typeof(JsonStringEnumConverter<AxisFeedbackUnit>))]
public enum AxisFeedbackUnit { EngineeringUnits, EncoderCounts }
[JsonConverter(typeof(JsonStringEnumConverter<RotaryMode>))]
public enum RotaryMode { Limited, Continuous, Modulo }
[JsonConverter(typeof(JsonStringEnumConverter<AxisHomeMode>))]
public enum AxisHomeMode { ControllerConfigured, Switch, Index, SwitchAndIndex }
[JsonConverter(typeof(JsonStringEnumConverter<AxisHomeDirection>))]
public enum AxisHomeDirection { Positive, Negative }

public sealed record AxisMechanics
{
    public AxisKind Kind { get; init; } = AxisKind.Linear;
    public AxisFeedbackUnit FeedbackUnit { get; init; } = AxisFeedbackUnit.EngineeringUnits;
    public double FeedbackCountsPerMotorRevolution { get; init; } = 131072;
    public double MotorRevolutions { get; init; } = 1;
    public double LoadRevolutions { get; init; } = 1;
    public double TravelPerLoadRevolution { get; init; } = 5;
    public bool ReverseDirection { get; init; }
    public RotaryMode RotaryMode { get; init; } = RotaryMode.Limited;
    public double ModuloStart { get; init; }
    public double ModuloPeriod { get; init; } = 360;
    public AxisHomeMode HomeMode { get; init; } = AxisHomeMode.ControllerConfigured;
    public AxisHomeDirection HomeDirection { get; init; } = AxisHomeDirection.Negative;
    public double HomeOffset { get; init; }
    public double HomeSearchVelocity { get; init; } = 1;
    public double HomeLatchVelocity { get; init; } = .2;
}

public static class AxisMechanicsValidator
{
    public static IReadOnlyList<string> Validate(AxisConfiguration axis)
    {
        ArgumentNullException.ThrowIfNull(axis);
        if (axis.Mechanics is not { } mechanics) return [];
        var errors = new List<string>();
        void Error(string message) => errors.Add($"轴 {axis.Name}: {message}");
        if (!Enum.IsDefined(mechanics.Kind) || !Enum.IsDefined(mechanics.FeedbackUnit) || !Enum.IsDefined(mechanics.RotaryMode) ||
            !Enum.IsDefined(mechanics.HomeMode) || !Enum.IsDefined(mechanics.HomeDirection)) Error("机械或回零枚举无效");
        if (mechanics.Kind == AxisKind.Linear && axis.Unit != "mm" || mechanics.Kind == AxisKind.Rotary && axis.Unit is not ("deg" or "rev"))
            Error("直线轴单位必须为 mm，旋转轴单位必须为 deg 或 rev");
        if (mechanics.Kind == AxisKind.Linear && mechanics.RotaryMode != RotaryMode.Limited) Error("直线轴不能使用连续或周期旋转模式");
        if (new[] { mechanics.FeedbackCountsPerMotorRevolution, mechanics.MotorRevolutions, mechanics.LoadRevolutions, mechanics.TravelPerLoadRevolution,
            mechanics.ModuloPeriod, mechanics.HomeSearchVelocity, mechanics.HomeLatchVelocity }.Any(value => !double.IsFinite(value) || value <= 0))
            Error("反馈计数、减速比、导程、周期和回零速度必须是正的有限数值");
        if (!double.IsFinite(axis.MinPosition) || !double.IsFinite(axis.MaxPosition) || axis.MinPosition >= axis.MaxPosition) Error("轴软限位必须为递增的有限数值");
        if (!double.IsFinite(mechanics.ModuloStart) || !double.IsFinite(mechanics.HomeOffset)) Error("周期起点和回零偏移必须为有限数值");
        if (mechanics.RotaryMode == RotaryMode.Modulo)
        {
            var end = mechanics.ModuloStart + mechanics.ModuloPeriod;
            if (!double.IsFinite(end) || mechanics.ModuloStart < axis.MinPosition || end > axis.MaxPosition) Error("完整旋转周期必须在轴软限位内");
        }
        if (mechanics.HomeMode != AxisHomeMode.ControllerConfigured && (mechanics.Kind == AxisKind.Linear || mechanics.RotaryMode == RotaryMode.Limited) &&
            (mechanics.HomeOffset < axis.MinPosition || mechanics.HomeOffset > axis.MaxPosition)) Error("回零偏移必须在有限行程内");
        if (mechanics.HomeLatchVelocity > mechanics.HomeSearchVelocity) Error("回零慢速不能超过搜索速度");
        if (mechanics.HomeMode != AxisHomeMode.ControllerConfigured && axis.MaximumVelocity is { } maximum &&
            (mechanics.HomeSearchVelocity > maximum || mechanics.HomeLatchVelocity > maximum)) Error("回零速度不能超过轴最大速度");
        if (errors.Count == 0)
        {
            var scale = AxisMechanicsConversion.CountsPerUnit(axis);
            if (!double.IsFinite(scale) || scale <= 0) Error("反馈计数换算结果必须是正的有限数值");
        }
        return errors.AsReadOnly();
    }
}

public static class AxisMechanicsConversion
{
    /// <summary>Mechanical feedback resolution for display. EngineeringUnits feedback must not be scaled again at runtime.</summary>
    public static double CountsPerUnit(AxisConfiguration axis)
    {
        ArgumentNullException.ThrowIfNull(axis);
        var mechanics = axis.Mechanics ?? throw new InvalidOperationException("轴未显式配置机械参数");
        var unitsPerRevolution = mechanics.Kind switch
        {
            AxisKind.Linear when axis.Unit == "mm" => mechanics.TravelPerLoadRevolution,
            AxisKind.Rotary when axis.Unit == "deg" => 360,
            AxisKind.Rotary when axis.Unit == "rev" => 1,
            _ => throw new InvalidDataException("轴类型和工程单位不匹配")
        };
        return mechanics.FeedbackCountsPerMotorRevolution * mechanics.MotorRevolutions / mechanics.LoadRevolutions / unitsPerRevolution;
    }
}

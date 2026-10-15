using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StWorkbench.Core;

public readonly record struct PathPoint(double X, double Y, double Z = 0);
public sealed record PathSegment(string Kind, PathPoint Start, PathPoint End, PathPoint Center = default, double SweepRadians = 0);
public sealed record PathProfile(double Velocity = 5, double Acceleration = 20, double Deceleration = 20, double Jerk = 100, double CornerTolerance = 0.05, double CycleSeconds = 0.001);
public sealed record MotionPathAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public uint Version { get; init; } = 1;
    public string Name { get; init; } = "Path1";
    public string SourceName { get; init; } = "";
    public string Layer { get; init; } = "0";
    public double ImportScale { get; init; } = 1;
    public string OriginalUnit { get; init; } = "drawing-unit";
    public string Mode { get; init; } = "Continuous";
    public string Unit { get; init; } = "mm";
    public string[] Axes { get; init; } = [];
    public PathSegment[] Segments { get; init; } = [];
    public PathProfile Profile { get; init; } = new();
    public PathPoint Offset { get; init; }
    public string ContentHash { get; init; } = "";
    public ulong NumericId => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(Id.ToByteArray().AsSpan(0, 8)) & 0x7FFF_FFFF_FFFF_FFFFUL;
    public override string ToString() => $"{Name} · v{Version} · {Segments.Length} 段";
    public MotionPathAsset Seal() => this with { ContentHash = CalculateHash(this) };
    public static string CalculateHash(MotionPathAsset path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(path with { ContentHash = "" })))).ToLowerInvariant();
    public static IReadOnlyList<string> Validate(MotionPathAsset? path)
    {
        var errors = new List<string>();
        if (path is null || path.Axes is null || path.Segments is null || path.Profile is null) return ["路径资源结构无效"];
        if (path.Id == Guid.Empty || path.NumericId == 0 || path.Version == 0 || !Regex.IsMatch(path.Name ?? "", "^[A-Za-z_][A-Za-z0-9_]*$")) errors.Add("路径 ID、版本或 ST 名称无效");
        if (path.Mode != "Continuous" || path.Unit != "mm") errors.Add("当前路径要求 Continuous 模式和 mm 单位");
        if (!double.IsFinite(path.ImportScale) || path.ImportScale <= 0 || path.ImportScale > 1000 || string.IsNullOrWhiteSpace(path.OriginalUnit)) errors.Add("DXF 导入单位或比例无效");
        if (path.Axes.Length is < 2 or > 3 || path.Axes.Any(string.IsNullOrWhiteSpace) || path.Axes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != path.Axes.Length) errors.Add("路径需要不重复的 XY 或 XYZ 工程轴映射");
        if (path.Segments.Length is < 1 or > 4096) errors.Add("路径段数量须为 1–4096");
        bool Finite(PathPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z) && Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) <= 1e6;
        if (!Finite(path.Offset) || path.Segments.Any(s => s is null || !Finite(s.Start) || !Finite(s.End) || !Finite(s.Center) || !double.IsFinite(s.SweepRadians) || s.Kind is not "Line" and not "Arc")) errors.Add("路径几何包含无效或不支持的数据");
        for (int i = 1; i < path.Segments.Length; i++) if (path.Segments[i - 1] is { } a && path.Segments[i] is { } b && a.End != b.Start) { errors.Add("不同轮廓不能自动连接；请为每条连续轮廓分别预存"); break; }
        var p = path.Profile;
        if (!double.IsFinite(p.Velocity) || !double.IsFinite(p.Acceleration) || !double.IsFinite(p.Deceleration) || !double.IsFinite(p.Jerk) || !double.IsFinite(p.CornerTolerance) || !double.IsFinite(p.CycleSeconds) || p.Velocity <= 0 || p.Acceleration <= 0 || p.Deceleration <= 0 || p.Jerk <= 0 || p.CornerTolerance < 0 || p.CornerTolerance > 100 || p.CycleSeconds is < 0.0001 or > 0.1) errors.Add("路径速度、加减速、跃度、容差或周期无效");
        if (errors.Count == 0 && !CalculateHash(path).Equals(path.ContentHash, StringComparison.Ordinal)) errors.Add("路径内容摘要不匹配，请重新预存版本");
        return errors;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace StWorkbench.Core;
public static class ProjectValidator
{
    public static IReadOnlyList<Diagnostic> Validate(StProject project, IReadOnlyList<SourceDocument> sources)
    {
        var errors = new List<Diagnostic>();
        void Error(string message, string path = "") => errors.Add(new(path, 1, 1, "error", message));
        if (!HasValidStructure(project) || sources is null || sources.Any(s => s is null || s.RelativePath is null || s.Text is null))
        {
            Error("工程包含空对象、集合或必填字段");
            return errors.AsReadOnly();
        }
        if (project.SchemaVersion != 1) Error("不支持的工程格式版本");
        if (project.Id == Guid.Empty || string.IsNullOrWhiteSpace(project.Name)) Error("工程 ID 和名称不能为空");
        if (project.Items.Count == 0) Error("工程必须包含源程序");
        var ids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in project.Items)
        {
            if (item.Id == Guid.Empty || !ids.Add(item.Id)) Error("程序对象 ID 为空或重复", item.SourcePath);
            if (!Regex.IsMatch(item.Name, @"^[A-Za-z_][A-Za-z0-9_]*$")) Error("ST 对象名称必须使用字母、数字、下划线，不能以数字开头", item.SourcePath);
            if (!names.Add(item.Name)) Error("对象重名（名称不区分大小写）", item.SourcePath);
            if (!Enum.IsDefined(item.Kind)) Error("未知的源对象类别", item.SourcePath);
            if (!IsSafeRelativePath(item.SourcePath) || !item.SourcePath.EndsWith(".st", StringComparison.OrdinalIgnoreCase)) Error("源文件必须是工程内的相对 .st 路径", item.SourcePath);
            if (!paths.Add(item.SourcePath.Replace('\\', '/'))) Error("源文件路径重复", item.SourcePath);
            var docs = sources.Where(s => s.ItemId == item.Id).ToArray();
            if (docs.Length != 1 || docs[0].RelativePath != item.SourcePath) Error("源文件缺失、重复或与工程路径不一致", item.SourcePath);
        }
        if (sources.Count != project.Items.Count || sources.Any(s => !ids.Contains(s.ItemId))) Error("源文件清单与工程对象不一致");
        var taskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in project.Tasks)
        {
            if (!Regex.IsMatch(task.Name, @"^[A-Za-z_][A-Za-z0-9_]*$") || !taskNames.Add(task.Name)) Error("任务名称无效或重复");
            if (task.IntervalMilliseconds < 1 || task.IntervalMilliseconds > 60000) Error("任务周期必须在 1–60000 ms 范围内");
            if (!project.Items.Any(i => i.Id == task.ProgramId && i.Kind == ItemKind.Program)) Error("任务必须绑定程序对象");
        }
        if (project.Tasks.Count(t => t.Enabled) != 1) Error("当前版本只支持一个启用的周期任务");
        var resourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceIds = new HashSet<Guid>();
        foreach (var device in project.Hardware.Devices)
        {
            if (device.Id == Guid.Empty || !deviceIds.Add(device.Id) || string.IsNullOrWhiteSpace(device.Name)) Error("EtherCAT 设备 ID 或名称无效");
            if (device.Entries.Any(e => e.BitLength is < 1 or > 65536)) Error("EtherCAT PDO 位宽无效");
        }
        errors.AddRange(NetworkTopologyValidator.Validate(project));
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var io in project.Hardware.Io)
        {
            if (!Regex.IsMatch(io.Name, @"^[A-Za-z_][A-Za-z0-9_]*$") || !resourceNames.Add(io.Name)) Error("IO 名称无效或重复");
            if (!Regex.IsMatch(io.Address, io.IsInput ? @"^%I[XWDB]\d+(\.\d+)?$" : @"^%Q[XWDB]\d+(\.\d+)?$", RegexOptions.IgnoreCase) || !addresses.Add(io.Address)) Error("IO 地址无效、方向不符或重复");
            if (!new[] { "BOOL", "BYTE", "WORD", "DWORD", "INT", "DINT", "REAL", "LREAL" }.Contains(io.Type.ToUpperInvariant())) Error("不支持的 IO 类型");
        }
        var controllerAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var axis in project.Hardware.Axes)
        {
            if (!Regex.IsMatch(axis.Name, @"^[A-Za-z_][A-Za-z0-9_]*$") || !resourceNames.Add(axis.Name) || string.IsNullOrWhiteSpace(axis.ControllerAxis) || !controllerAxes.Add(axis.ControllerAxis)) Error("轴名称/控制器轴引用无效或重复");
            if (!double.IsFinite(axis.MinPosition) || !double.IsFinite(axis.MaxPosition) || axis.MinPosition >= axis.MaxPosition || string.IsNullOrWhiteSpace(axis.Unit)) Error("轴单位或限位无效");
            if (new[] { axis.MaximumVelocity, axis.MaximumAcceleration, axis.MaximumDeceleration, axis.MaximumJerk }.Any(v => v.HasValue && (!double.IsFinite(v.Value) || v.Value <= 0))) Error("轴动态限制必须是正的有限数值");
        }
        if (project.Paths.Count > 64) Error("工程路径版本超过 64，请拆分工程");
        var pathVersions = new HashSet<(Guid, uint)>();
        var pathNames = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var numericIds = new Dictionary<ulong, Guid>();
        foreach (var path in project.Paths)
        {
            foreach (var message in MotionPathAsset.Validate(path)) Error(message, "paths/" + path.Name);
            if (!pathVersions.Add((path.Id, path.Version))) Error("路径 ID/版本重复");
            if (pathNames.TryGetValue(path.Name, out var owner) && owner != path.Id) Error("不同路径资源不能使用同一个 ST 名称");
            else pathNames[path.Name] = path.Id;
            if (numericIds.TryGetValue(path.NumericId, out owner) && owner != path.Id) Error("路径控制器编号冲突");
            else numericIds[path.NumericId] = path.Id;
            if (path.Axes.Any(a => !project.Hardware.Axes.Any(axis => axis.Name.Equals(a, StringComparison.Ordinal)))) Error("路径引用的工程轴不存在", "paths/" + path.Name);
        }
        if (errors.Count == 0 && Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(project.Paths)) > 2 * 1024 * 1024) Error("工程内路径资产超过 2 MiB，请拆分路径或工程");
        errors.AddRange(PathBindingValidator.Validate(sources, project.Paths));
        foreach (var message in MotionCycleValidator.Validate(project.MotionCycle, project.Hardware.Axes, project.Paths)) Error(message);
        errors.AddRange(AxisControlBinding.Validate(sources, project.Hardware.Axes, project.Tasks.Select(t => new TaskSnapshot(t.Name, t.ProgramId, t.IntervalMilliseconds, t.Enabled)).ToArray()));
        return errors.AsReadOnly();
    }
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':')) return false;
        return path.Replace('\\', '/').Split('/').All(p => p.Length > 0 && p is not "." and not ".." && !p.EndsWith('.') && !p.EndsWith(' ') && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase));
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static bool HasValidStructure(StProject? project) =>
        project is not null && project.Name is not null && project.Items is not null && project.Tasks is not null &&
        project.Hardware is not null && project.Hardware.Io is not null && project.Hardware.Axes is not null && project.Hardware.Devices is not null &&
        project.Hardware.Devices.All(d => d is not null && d.Name is not null && d.Entries is not null && d.Entries.All(e => e is not null && e.Name is not null && e.Type is not null)) &&
        project.Items.All(i => i is not null && i.Name is not null && i.SourcePath is not null) &&
        project.Tasks.All(t => t is not null && t.Name is not null) &&
        project.Hardware.Io.All(i => i is not null && i.Name is not null && i.Address is not null && i.Type is not null) &&
        project.Hardware.Axes.All(a => a is not null && a.Name is not null && a.ControllerAxis is not null && a.Unit is not null) &&
        project.Paths is not null && project.Paths.All(p => p is not null && p.Name is not null && p.Axes is not null && p.Segments is not null && p.Profile is not null) &&
        (project.MotionCycle is null || project.MotionCycle.SchedulingPolicy is not null && project.MotionCycle.AxisNames is not null && project.MotionCycle.AxisNames.All(n => n is not null));
    public static BuildSnapshot Capture(StProject project, IReadOnlyList<SourceDocument> sources)
    {
        var errors = Validate(project, sources);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Select(e => e.Message)));
        var copy = Array.AsReadOnly(sources.OrderBy(s => s.RelativePath, StringComparer.Ordinal).Select(s => s with { }).ToArray());
        var tasks = Array.AsReadOnly(project.Tasks.Select(t => new TaskSnapshot(t.Name, t.ProgramId, t.IntervalMilliseconds, t.Enabled)).ToArray());
        var pathAssets = Array.AsReadOnly(project.Paths.Select(p => p with { Axes = p.Axes.ToArray(), Segments = p.Segments.ToArray(), Profile = p.Profile with { } }).ToArray());
        var identity = pathAssets.Count == 0
            ? JsonSerializer.Serialize(new { project.Id, project.Name, Sources = copy, Tasks = tasks })
            : JsonSerializer.Serialize(new { project.Id, project.Name, Sources = copy, Tasks = tasks, Paths = pathAssets });
        var cycle = project.MotionCycle is { Enabled: true } enabledCycle ? enabledCycle with { AxisNames = enabledCycle.AxisNames.ToArray() } : null;
        var hardwareIdentity = cycle is null ? JsonSerializer.Serialize(project.Hardware) : JsonSerializer.Serialize(new { Hardware = project.Hardware, MotionCycle = cycle });
        string? configurationJson = null;
        if (project.Network is not null || project.Hardware.Stations is not null)
            hardwareIdentity = configurationJson = JsonSerializer.Serialize(new HardwareConfigurationDocument(project.Hardware) { Network = project.Network, MotionCycle = cycle });
        return new(project.Id, project.Name, Hash(identity), Hash(hardwareIdentity), copy, tasks)
        { HardwareConfigurationJson = configurationJson, Paths = pathAssets, Axes = Array.AsReadOnly(project.Hardware.Axes.Select(a => a with { }).ToArray()), MotionCycle = cycle };
    }
}


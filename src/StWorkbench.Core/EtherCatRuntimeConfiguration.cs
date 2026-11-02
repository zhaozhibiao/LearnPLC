using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace StWorkbench.Core;

public sealed record EtherCatRuntimeSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public EtherCatAxisTraceSettings? AxisTrace { get; init; }
    public uint CycleTimeNanoseconds { get; init; } = 1_000_000;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public EtherCatCycleTimingSettings? CycleTiming { get; init; }
}
public sealed record EtherCatAxisTraceSettings
{
    public bool Enabled { get; init; }
    public int Capacity { get; init; }
}
public sealed record EtherCatCycleTimingSettings
{
    public bool Enabled { get; init; }
    public string Model { get; init; } = "R2M";
    public uint BoundaryGuardNs { get; init; } = 200_000;
    public uint RemainingSendBudgetNs { get; init; } = 100_000;
    public uint RequiredGoodCycles { get; init; } = 1000;
}
public sealed record EtherCatStationRuntimeSettings
{
    public IReadOnlyList<ushort>? SelectedPdoIndices { get; init; }
    public string? DcModeName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DcSync0ShiftNanoseconds { get; init; }
    public string? ProfileId { get; init; }
}
public sealed record EtherCatAxisRuntimeSettings
{
    public string ProfileId { get; init; } = "cia402-csp";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DriveDemandProtection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DriveErrorOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DemandDeliveryEnforced { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? DemandDeliveryTolerance { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? VelocityFeedbackRawPerUnitPerSecond { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? VelocityOffsetRawPerUnitPerSecond { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? VelocityFeedforwardGain { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FollowingErrorRawPerUnit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PositionDemandRawPerUnit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public uint? DriveFollowingErrorWindowRaw { get; init; }
    public string MotorModel { get; init; } = "";
    public bool MechanicsConfirmed { get; init; }
    public bool SafetyConfirmed { get; init; }
    public string VelocityUnits { get; init; } = "CountsPerSecond";
    public double MaximumActualVelocity { get; init; }
    public double MaximumFollowingError { get; init; }
    public int FollowingErrorTimeoutMilliseconds { get; init; }
    public EtherCatAxisHomingSettings? Homing { get; init; }
    public EtherCatMotionCompletionSettings? MotionCompletion { get; init; }
}
public sealed record EtherCatAxisHomingSettings
{
    public bool Required { get; init; } = true;
    public int Method { get; init; }
    public bool AllowCurrentPositionZero { get; init; }
    public uint SearchSpeedRaw { get; init; }
    public uint LatchSpeedRaw { get; init; }
    public uint AccelerationRaw { get; init; }
    public int OffsetRaw { get; init; }
    public uint TimeoutMilliseconds { get; init; }
}
public sealed record EtherCatMotionCompletionSettings
{
    public double PositionTolerance { get; init; }
    public double VelocityTolerance { get; init; }
    public uint DwellMilliseconds { get; init; }
    public bool PositionStabilityCompletion { get; init; }
}
public sealed record EtherCatRuntimeCompilation(bool ReadyForRuntime, IReadOnlyList<string> Blockers,
    IReadOnlyList<Diagnostic> Diagnostics, string TopologyJson, string MachineJson, string IoMapJson, string TopologyFingerprint);

/// <summary>Compiles explicit engineering settings. ReadyForRuntime means schema completeness, never live commissioning proof.</summary>
public static class EtherCatRuntimeCompiler
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    public static string CanonicalJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            void Write(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(property.Name); Write(property.Value); }
                    writer.WriteEndObject();
                }
                else if (element.ValueKind == JsonValueKind.Array)
                { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item); writer.WriteEndArray(); }
                else element.WriteTo(writer);
            }
            Write(value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string EntryKey(string stationName, ushort index, byte subIndex) => $"{stationName}.0x{index:X4}:{subIndex}";
    public static EtherCatRuntimeCompilation Compile(StProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var blockers = new List<string>();
        void Block(string message) { if (!blockers.Contains(message)) blockers.Add(message); }
        foreach (var error in NetworkTopologyValidator.Validate(project)) Block(error.Message);
        var settings = project.Hardware.Runtime;
        if (settings is null) Block("必须显式启用通用 EtherCAT 运行配置。");
        var cycle = settings?.CycleTimeNanoseconds ?? 1_000_000;
        if (cycle is < 100_000 or > 1_000_000_000 || cycle % 1000 != 0) Block("运行周期必须为 100–1000000 μs 的整数微秒。");
        if (project.MotionCycle is { Enabled: true } mc && (long)mc.PeriodMicroseconds * 1000 != cycle) Block("运动周期与 EtherCAT 周期不一致。");
        var stations = project.Hardware.Stations ?? [];
        var trace = settings?.AxisTrace;
        if (trace is { Enabled: true } && (trace.Capacity is < 1 or > 120000 || project.Hardware.Axes.Count != 1))
            Block("Axis trace requires exactly one axis and explicit capacity 1-120000.");
        var timing = settings?.CycleTiming;
        if (timing is { Enabled: true } && (timing.Model != "R2M" || timing.BoundaryGuardNs == 0 || timing.RemainingSendBudgetNs == 0 || timing.RequiredGoodCycles == 0 || (ulong)timing.BoundaryGuardNs * 2 >= cycle || (ulong)timing.BoundaryGuardNs * 2 + timing.RemainingSendBudgetNs > cycle))
            Block("R2M 时序模型、周期裕量和连续就绪周期数无效。");
        if (stations.Count is < 1 or > 64) Block("通用运行配置必须有 1–64 个从站。");
        var slaves = new List<object>(); var machineSlaves = new List<object>();
        var selectedEntries = new Dictionary<Guid, IReadOnlyList<PdoEntry>>();
        var totalEntries = 0; var totalPdos = 0; var totalSdoBytes = 0;
        for (var position = 0; position < stations.Count; position++)
        {
            var station = stations[position];
            if (station is null) continue;
            var device = project.Hardware.Devices.FirstOrDefault(d => d.Id == station.DeviceId);
            if (device is null) continue;
            var is620nProfile = station.Runtime?.ProfileId == "is620n-csp";
            var knownIs620n = device.VendorId == 0x00100000 && device.ProductCode == 0x000C0108 && device.Revision == 0x00010001;
            if (station.AliasAddress != 0) Block($"从站 {station.Name}: 当前运行端不支持别名寻址。");
            if (station.Runtime?.ProfileId is { Length: > 0 } profile && profile is not ("cia402-csp" or "is620n-csp")) Block($"从站 {station.Name}: 未支持的运行配置档 {profile}。");
            if (is620nProfile && !knownIs620n) Block($"从站 {station.Name}: IS620N 专用 PDO 仅适用于已验证的厂商、产品和修订号。");
            if (device.Description is not { } description) { Block($"从站 {station.Name}: 旧设备目录缺少完整 ESI 描述，请重新导入。"); continue; }
            foreach (var unsupported in description.UnsupportedFeatures) Block($"从站 {station.Name}: 未支持的 ESI 描述 {unsupported}。");
            var requested = station.Runtime?.SelectedPdoIndices;
            if (requested is not null && (requested.Distinct().Count() != requested.Count || requested.Any(i => !description.PdoGroups.Any(g => g.Index == i)))) Block($"从站 {station.Name}: PDO 组选择包含重复或未知索引。");
            var groups = description.PdoGroups.Where(g => requested is null ? g.DefaultSelected : requested.Contains(g.Index)).OrderBy(g => g.SyncManager).ThenBy(g => g.Index).ToArray();
            if (groups.Length == 0 && !is620nProfile) Block($"从站 {station.Name}: 未选择 PDO 组。");
            if (!is620nProfile && description.PdoGroups.Any(g => g.Mandatory && !groups.Contains(g))) Block($"从站 {station.Name}: 必选 PDO 组未启用。");
            if (!is620nProfile && groups.Any(g => groups.Any(other => other.Index != g.Index && (g.Excludes.Contains(other.Index) || other.Excludes.Contains(g.Index))))) Block($"从站 {station.Name}: PDO 组互斥冲突。");
            var entries = groups.SelectMany(g => g.Entries).ToArray();
            if (station.EnabledEntries is null || !station.EnabledEntries.OrderBy(e => e.Index).ThenBy(e => e.SubIndex).ThenBy(e => e.IsInput).SequenceEqual(entries.OrderBy(e => e.Index).ThenBy(e => e.SubIndex).ThenBy(e => e.IsInput))) Block($"从站 {station.Name}: 启用 PDO 项必须与完整 PDO 组选择一致。");
            if (entries.GroupBy(e => (e.Index, e.SubIndex)).Any(g => g.Count() > 1)) Block($"从站 {station.Name}: PDO 对象重复，无法唯一注册。");
            if (entries.Any(e => e.Index == 0 || e.BitLength is < 1 or > 64)) Block($"从站 {station.Name}: 运行端不支持填充 PDO 或超出 1–64 位的项。");
            IReadOnlyList<PdoEntry> runtimeEntries = entries;
            object[] runtimePdos = groups.Select(g => (object)new { direction = g.IsInput ? "Tx" : "Rx", syncManager = g.SyncManager ?? 0, index = g.Index, entries = g.Entries.Select(e => new { index = e.Index, subindex = e.SubIndex, bitLength = e.BitLength, key = EntryKey(station.Name, e.Index, e.SubIndex) }).ToArray() }).ToArray();
            object[] runtimeManagers;
            var managers = new List<object>();
            foreach (var group in groups.GroupBy(g => g.SyncManager))
            {
                if (group.Key is null or < 2 or > 15 || group.Select(g => g.IsInput).Distinct().Count() != 1) { Block($"从站 {station.Name}: PDO 缺少有效且方向一致的 SyncManager。"); continue; }
                var manager = description.SyncManagers.FirstOrDefault(s => s.Index == group.Key);
                if (manager is null || manager.Enable == false) Block($"从站 {station.Name}: SyncManager 未声明或已禁用。");
                managers.Add(new { index = group.Key, direction = group.First().IsInput ? "Tx" : "Rx", watchdog = manager?.Watchdog switch { true => "Enable", false => "Disable", _ => "Default" } });
            }
            runtimeManagers = managers.ToArray();
            if (is620nProfile && knownIs620n)
            {
                (runtimeEntries, runtimePdos, runtimeManagers) = BuildIs620nPdoProfile(station.Name, description, Block);
                selectedEntries[station.Id] = runtimeEntries;
            }
            else
            {
                selectedEntries[station.Id] = runtimeEntries;
            }
            totalEntries += runtimeEntries.Count;
            totalPdos += runtimePdos.Length;
            var startup = new List<object>();
            if (description.StartupCommands.Count + (project.Hardware.Axes.Any(a => a.StationId == station.Id && a.Runtime?.DriveFollowingErrorWindowRaw is not null) ? 1 : 0) > 256) Block($"从站 {station.Name}: 启动 SDO 超过 256 项。");
            foreach (var sdo in description.StartupCommands)
            {
                if (sdo.Transition != "PS" || sdo.Index == 0 || sdo.CompleteAccess && sdo.SubIndex != 0) Block($"从站 {station.Name}: 仅支持有效 PS 启动 SDO。");
                if (sdo.Index is 0x6040 or 0x607A or 0x60FF or 0x60B1 or 0x6071) Block($"从站 {station.Name}: 启动 SDO 不允许写控制字或运动目标。");
                byte[] data;
                try { data = Convert.FromHexString(sdo.DataHex); } catch (FormatException) { data = []; Block($"从站 {station.Name}: SDO 数据无效。"); }
                if (data.Length is < 1 or > 1024) Block($"从站 {station.Name}: SDO 数据超出 1–1024 字节。");
                totalSdoBytes += data.Length;
                startup.Add(new { index = sdo.Index, subindex = sdo.SubIndex, data = data.Select(b => (int)b).ToArray(), completeAccess = sdo.CompleteAccess, transition = sdo.Transition });
            }
            var windows = project.Hardware.Axes.Where(a => a.StationId == station.Id && a.Runtime?.DriveFollowingErrorWindowRaw is not null).Select(a => a.Runtime!.DriveFollowingErrorWindowRaw!.Value).ToArray();
            if (windows.Length > 1) Block($"从站 {station.Name}: 驱动跟随窗口配置重复。");
            if (windows.Length == 1)
            {
                if (!is620nProfile || !knownIs620n || windows[0] == 0) Block($"从站 {station.Name}: 6065 启动窗口要求已验证的 IS620N 配置及正的原始计数。");
                if (description.StartupCommands.Any(s => s.Index == 0x6065)) Block($"从站 {station.Name}: 6065 工程窗口与 ESI 启动项冲突。");
                var window = windows[0]; totalSdoBytes += 4;
                startup.Add(new { index = 0x6065, subindex = 0, data = new[] { (int)(window & 255), (int)((window >> 8) & 255), (int)((window >> 16) & 255), (int)((window >> 24) & 255) }, completeAccess = false, transition = "PS" });
            }
            var mode = station.Runtime?.DcModeName is { Length: > 0 } modeName ? description.DcModes.FirstOrDefault(d => d.Name == modeName) : null;
            if (station.Runtime?.DcModeName is { Length: > 0 } && mode is null) Block($"从站 {station.Name}: 未找到所选 DC 模式。");
            if (description.DcModes.Count > 0 && mode is null) Block($"从站 {station.Name}: 必须显式选择 ESI DC 模式。");
            long Timing(EtherCatDcTiming? timing)
            {
                if (timing is null) return 0;
                // EtherCAT ESI: positive factor multiplies application cycle; negative factor divides it.
                var value = timing.Factor is { } f && f != 0 ? f > 0 ? (long)cycle * f : cycle / -(long)f : timing.Value;
                if (timing.Factor is < 0 && cycle % -(long)timing.Factor.Value != 0) Block($"从站 {station.Name}: DC 周期因子不能整除应用周期。");
                if (value is < 0 or > uint.MaxValue) { Block($"从站 {station.Name}: DC 周期超出范围。"); return 0; }
                return value;
            }
            var sync0 = Timing(mode?.Sync0); var sync1 = Timing(mode?.Sync1);
            if (mode is not null && mode.AssignActivate != 0 && sync0 != cycle) Block($"从站 {station.Name}: DC Sync0 周期必须等于运行周期。");
            if (mode?.Sync1?.Shift is not (null or 0)) Block($"从站 {station.Name}: 当前运行端不支持 DC Sync1 偏移。");
            if (mode?.Sync0?.Shift is < int.MinValue or > int.MaxValue) Block($"从站 {station.Name}: DC Sync0 偏移超出范围。");
            slaves.Add(new { position, vendorId = device.VendorId, productCode = device.ProductCode, revisionNo = device.Revision, name = station.Name,
                dc = new { enabled = mode is not null && mode.AssignActivate != 0, assignActivate = mode?.AssignActivate ?? 0, sync0CycleNs = sync0, sync0ShiftNs = station.Runtime?.DcSync0ShiftNanoseconds ?? mode?.Sync0?.Shift ?? 0, sync1CycleNs = sync1 },
                moduleIdents = Array.Empty<uint>(), startupSdos = startup, syncManagers = runtimeManagers,
                pdos = runtimePdos });
            machineSlaves.Add(new { position, role = project.Hardware.Axes.Any(a => a.StationId == station.Id) ? "servo" : "io", vendor = device.VendorId.ToString(), model = device.Name, identitySource = "ESI:" + description.SourceSha256, vendorId = $"0x{device.VendorId:X8}", productCode = $"0x{device.ProductCode:X8}", revisionNumber = $"0x{device.Revision:X8}", pdoProfile = station.Runtime?.ProfileId ?? "generic-io" });
        }
        if (totalEntries > 4096 || totalPdos > 1024 || totalSdoBytes > 65536) Block("运行配置超过 PDO 或启动 SDO 总量限制。");
        if (timing is { Enabled: true })
        {
            var reference = stations.FirstOrDefault();
            long? referenceShift = null;
            foreach (var st in stations.Where(st => st == reference || project.Hardware.Axes.Any(a => a.StationId == st.Id)))
            {
                var desc = project.Hardware.Devices.FirstOrDefault(d => d.Id == st.DeviceId)?.Description;
                var dc = desc?.DcModes.FirstOrDefault(m => m.Name == st.Runtime?.DcModeName);
                var shift = st.Runtime?.DcSync0ShiftNanoseconds ?? dc?.Sync0?.Shift ?? 0;
                referenceShift ??= shift;
                if (dc is null || dc.AssignActivate == 0 || shift < 0 || shift != referenceShift || (ulong)shift >= cycle)
                    Block($"从站 {st.Name}: R2M 要求轴及参考从站具有一致的有效 DC 偏移和周期裕量。");
            }
            if (project.Hardware.Axes.Count == 0) Block("R2M 时序模型要求工程轴。");
        }
        var axes = CompileAxes(project, stations, selectedEntries, Block);
        var ioGroups = CompileIo(project, stations, Block);
        var machine = Serialize(new { schemaVersion = 1, cycleTimeMicroseconds = cycle / 1000, commandWatchdogMilliseconds = 500, safeOutputPolicy = "all-digital-outputs-off", cycleTiming = timing, axisTrace = trace, requiredRuntimeCapabilities = trace is { Enabled: true } ? new[] { "workbench_motion_migration_v1", "workbench_axis_trace_v1" } : RequiresMotionMigration(project) ? new[] { "workbench_motion_migration_v1" } : null, slaves = machineSlaves, axes, digitalOutputs = Array.Empty<object>() });
        var io = Serialize(new { schemaVersion = 1, groups = ioGroups });
        Dictionary<string, object> Topology() => new() { ["schemaVersion"] = 1, ["readyForRuntime"] = blockers.Count == 0, ["blockers"] = blockers.ToArray(), ["cycleTimeNs"] = cycle,
            ["machineSha256"] = Hash(machine), ["ioMapSha256"] = Hash(io), ["slaves"] = slaves };
        var topologyObject = Topology();
        if (new[] { Serialize(topologyObject), machine, io }.Any(s => Encoding.UTF8.GetByteCount(s) > 4 * 1024 * 1024)) { Block("运行配置文档超过 4 MiB。"); topologyObject = Topology(); }
        using var unsignedTopology = JsonDocument.Parse(Serialize(topologyObject));
        var fingerprint = Hash(CanonicalJson(unsignedTopology.RootElement));
        topologyObject["topologyFingerprint"] = fingerprint;
        var topology = Serialize(topologyObject);
        return new(blockers.Count == 0, blockers.AsReadOnly(), blockers.Select(b => new Diagnostic("ethercat-runtime", 1, 1, "error", b)).ToArray(), topology, machine, io, fingerprint);
    }

    private static (IReadOnlyList<PdoEntry> Entries, object[] Pdos, object[] SyncManagers) BuildIs620nPdoProfile(
        string stationName, EtherCatDeviceDescription description, Action<string> block)
    {
        var outputSm = description.SyncManagers.FirstOrDefault(manager => manager.Index == 2);
        var inputSm = description.SyncManagers.FirstOrDefault(manager => manager.Index == 3);
        if (outputSm is null || inputSm is null || outputSm.Enable == false || inputSm.Enable == false)
        {
            block($"从站 {stationName}: IS620N CSP 要求 SyncManager 2 和 3 已声明并启用。");
            return (Array.Empty<PdoEntry>(), Array.Empty<object>(), Array.Empty<object>());
        }
        object MapEntry(PdoEntry entry) => new { index = entry.Index, subindex = entry.SubIndex,
            bitLength = entry.BitLength, key = EntryKey(stationName, entry.Index, entry.SubIndex) };
        var outputs = new PdoEntry[] {
            new(0x6040, 0, 16, "Controlword", "UINT", false),
            new(0x607A, 0, 32, "TargetPosition", "DINT", false),
            new(0x60B1, 0, 32, "VelocityOffset", "DINT", false),
            new(0x6060, 0, 8, "ModeOfOperation", "SINT", false)
        };
        var inputs = new PdoEntry[] {
            new(0x603F, 0, 16, "ErrorCode", "UINT", true),
            new(0x6041, 0, 16, "Statusword", "UINT", true),
            new(0x6064, 0, 32, "ActualPosition", "DINT", true),
            new(0x606C, 0, 32, "ActualVelocity", "DINT", true),
            new(0x6062, 0, 32, "PositionDemand", "DINT", true),
            new(0x60F4, 0, 32, "FollowingError", "DINT", true),
            new(0x6061, 0, 8, "ModeDisplay", "SINT", true)
        };
        var pdos = new object[] {
            new { direction = "Rx", syncManager = 2, index = 0x1600, entries = outputs.Select(MapEntry).ToArray() },
            new { direction = "Tx", syncManager = 3, index = 0x1A00, entries = inputs.Select(MapEntry).ToArray() }
        };
        string Watchdog(EtherCatSyncManager manager) => manager.Watchdog switch {
            true => "Enable", false => "Disable", _ => "Default"
        };
        var managers = new object[] {
            new { index = 2, direction = "Rx", watchdog = Watchdog(outputSm) },
            new { index = 3, direction = "Tx", watchdog = Watchdog(inputSm) }
        };
        return (outputs.Concat(inputs).ToArray(), pdos, managers);
    }

    private static IReadOnlyList<object> CompileIo(StProject project, IReadOnlyList<EtherCatStation> stations, Action<string> block)
    {
        var groups = new List<object>(); var occupied = new HashSet<string>(StringComparer.Ordinal);
        var axisIds = project.Hardware.Axes.Where(a => a.StationId is not null).Select(a => a.StationId!.Value).ToHashSet();
        foreach (var mapping in project.Hardware.Io)
        {
            var station = stations.FirstOrDefault(s => s.Id == mapping.StationId);
            if (station is null || mapping.PdoIndex is null || mapping.PdoSubIndex is null) { block($"IO {mapping.Name}: 必须绑定具体从站 PDO。"); continue; }
            if (axisIds.Contains(station.Id)) { block($"IO {mapping.Name}: 轴从站的 PDO 由运动控制独占，请移除普通 IO 映射。"); continue; }
            var encoding = mapping.Type.ToUpperInvariant() switch { "BOOL" => "PackedBoolean", "INT" => "Int16", "REAL" => "Float32", _ => null };
            if (encoding is null) { block($"IO {mapping.Name}: 当前运行端仅支持 BOOL、INT、REAL 映射。"); continue; }
            var key = EntryKey(station.Name, mapping.PdoIndex.Value, mapping.PdoSubIndex.Value);
            if (!occupied.Add(key)) block($"IO {mapping.Name}: PDO 物理位重复映射。");
            groups.Add(new { namePrefix = mapping.Name, direction = mapping.IsInput ? "Input" : "Output", encoding, channelCount = 1, channelsPerEntry = 1, sourceEntries = new[] { key }, unit = encoding == "PackedBoolean" ? "bool" : "raw", commissioningRequired = false, safeValue = mapping.IsInput ? (int?)null : 0 });
        }
        if (groups.Count > 512) block("IO 逻辑通道超过 512。");
        return groups;
    }

    private static IReadOnlyList<object> CompileAxes(StProject project, IReadOnlyList<EtherCatStation> stations, Dictionary<Guid, IReadOnlyList<PdoEntry>> selected, Action<string> block)
    {
        var result = new List<object>(); var used = new HashSet<Guid>();
        foreach (var axis in project.Hardware.Axes)
        {
            var invalid = false;
            void Error(string text) { invalid = true; block($"轴 {axis.Name}: {text}"); }
            var runtime = axis.Runtime; var mechanics = axis.Mechanics;
            var station = stations.FirstOrDefault(s => s.Id == axis.StationId);
            if (runtime is null || mechanics is null || station is null) { Error("必须显式配置从站绑定、机械参数和运行设置。"); continue; }
            if (!used.Add(station.Id)) Error("同一从站不能绑定多个轴。");
            foreach (var issue in AxisMechanicsValidator.Validate(axis)) { invalid = true; block(issue); }
            if (string.IsNullOrWhiteSpace(axis.Name) || project.Hardware.Axes.Count(a => a.Name == axis.Name) != 1) Error("轴名称为空或重复。");
            if (axis.ControllerAxis is null || !System.Text.RegularExpressions.Regex.IsMatch(axis.ControllerAxis, @"^[A-Za-z_][A-Za-z0-9_]{0,63}\z") || project.Hardware.Axes.Count(a => a.ControllerAxis == axis.ControllerAxis) != 1) Error("原生控制器轴名称必须为唯一的 1–64 个 ASCII 标识符字符。");
            if (!runtime.MechanicsConfirmed || !runtime.SafetyConfirmed || string.IsNullOrWhiteSpace(runtime.MotorModel) || runtime.MotorModel == "CONFIGURE_BEFORE_ENABLE") Error("必须确认机械及安全参数并填写电机型号。");
            if (mechanics.FeedbackUnit != AxisFeedbackUnit.EncoderCounts) Error("通用 CiA402 反馈必须显式配置为编码器计数。");
            if (mechanics.RotaryMode != RotaryMode.Limited) Error("当前原生运行端仅支持有限行程轴，不支持连续或周期旋转语义。");
            if (mechanics.HomeMode != AxisHomeMode.ControllerConfigured) Error("机械属性中的开关/索引回零尚未映射到原生运行端；请选择控制器配置，并在轴运行参数中显式配置回零。");
            if (runtime.ProfileId is not ("cia402-csp" or "is620n-csp") || station.Runtime?.ProfileId != runtime.ProfileId) Error("轴及从站必须选择一致的已支持 CSP 配置档。");
            if (runtime.ProfileId == "is620n-csp")
            {
                var identity = project.Hardware.Devices.FirstOrDefault(d => d.Id == station.DeviceId);
                if (identity is not { VendorId: 0x00100000, ProductCode: 0x000C0108, Revision: 0x00010001 }) Error("IS620N 专用配置档仅适用于已有证据的厂商、产品和修订号；其他驱动请选择通用 CSP。");
            }
            if (runtime.VelocityUnits != "CountsPerSecond") Error("当前运行端仅支持每秒计数速度单位。");
            if (new[] { axis.MaximumVelocity, axis.MaximumAcceleration, axis.MaximumDeceleration }.Any(v => v is null || !double.IsFinite(v.Value) || v <= 0)) Error("必须显式配置正的速度、加速度和减速度限制。");
            if (!Positive(runtime.MaximumActualVelocity) || !Positive(runtime.MaximumFollowingError) || runtime.FollowingErrorTimeoutMilliseconds is < 1 or > 60000) Error("实际速度及跟随误差安全参数无效。");
            if (axis.MaximumJerk is { } jerk && (!double.IsFinite(jerk) || jerk < 0)) Error("加加速度上限必须为非负有限工程单位/s³；0 或未设置时沿用旧规划。");
            if (runtime.DriveErrorOnly == true && runtime.DriveDemandProtection != true) Error("驱动误差辅助监控需要启用驱动需求保护模式。");
            if (runtime.DriveDemandProtection == true && (runtime.FollowingErrorRawPerUnit is not { } feScale || !Positive(feScale) || runtime.PositionDemandRawPerUnit is not { } demandScale || !Positive(demandScale))) Error("辅助监控必须声明 60F4 与 6062 的正有限原始计数/工程单位比例。");
            foreach (var scale in new[] { runtime.VelocityFeedbackRawPerUnitPerSecond, runtime.VelocityOffsetRawPerUnitPerSecond, runtime.FollowingErrorRawPerUnit, runtime.PositionDemandRawPerUnit })
                if (scale is { } value && !Positive(value)) Error("反馈与前馈比例必须为正有限数值。");
            if (runtime.VelocityFeedforwardGain is { } gain && (!double.IsFinite(gain) || gain is < 0 or > 1)) Error("速度前馈增益必须为 [0,1] 范围内的有限数值。");
            if (runtime.DemandDeliveryTolerance is { } tolerance && (!double.IsFinite(tolerance) || tolerance < 0)) Error("交付诊断容差必须为非负有限工程单位。");
            void RawRange(double magnitude, double? scale, string name)
            {
                if (scale is { } factor && (!double.IsFinite(magnitude * factor) || Math.Abs(magnitude * factor) > int.MaxValue)) Error(name + " 换算超出 int32 原始值范围。");
            }
            RawRange(Math.Max(Math.Abs(axis.MinPosition), Math.Abs(axis.MaxPosition)), runtime.PositionDemandRawPerUnit, "6062位置需求");
            RawRange(runtime.MaximumFollowingError, runtime.FollowingErrorRawPerUnit, "60F4辅助窗口");
            RawRange(runtime.MaximumActualVelocity, runtime.VelocityFeedbackRawPerUnitPerSecond, "606C实际速度保护");
            RawRange((axis.MaximumVelocity ?? 0) * (runtime.VelocityFeedforwardGain ?? 1), runtime.VelocityOffsetRawPerUnitPerSecond, "速度前馈");
            var homing = runtime.Homing;
            if (homing is null || !homing.Required || homing.Method is < 1 or > 35 || homing.Method == 35 && (runtime.ProfileId != "is620n-csp" || !homing.AllowCurrentPositionZero) || homing.Method != 35 && homing.AllowCurrentPositionZero || homing.SearchSpeedRaw == 0 || homing.LatchSpeedRaw == 0 || homing.AccelerationRaw == 0 || homing.LatchSpeedRaw > homing.SearchSpeedRaw || homing.TimeoutMilliseconds is < 100 or > 600000) Error("必须显式配置已支持的安全回零方法；方法 35 仅允许 IS620N 显式当前位置置零。");
            var completion = runtime.MotionCompletion;
            if (completion is null || !Positive(completion.PositionTolerance) || !Positive(completion.VelocityTolerance) || completion.DwellMilliseconds == 0) Error("必须显式配置运动完成容差和稳定时间。");
            var pdo = new Dictionary<string, string>();
            var velocitySetpoint = runtime.ProfileId == "is620n-csp"
                ? (Name: "velocityOffset", Index: (ushort)0x60B1)
                : (Name: "targetVelocity", Index: (ushort)0x60FF);
            var required = new (string Name, ushort Index, int Width, bool Input, string[] Types)[] {
                ("controlword",0x6040,16,false,["UINT","UINT16","WORD"]), ("targetPosition",0x607A,32,false,["DINT","INT32"]),
                (velocitySetpoint.Name,velocitySetpoint.Index,32,false,["DINT","INT32"]), ("modeOfOperation",0x6060,8,false,["SINT","INT8"]),
                ("statusword",0x6041,16,true,["UINT","UINT16","WORD"]), ("positionActual",0x6064,32,true,["DINT","INT32"]),
                ("velocityActual",0x606C,32,true,["DINT","INT32"]), ("modeDisplay",0x6061,8,true,["SINT","INT8"]), ("errorCode",0x603F,16,true,["UINT","UINT16","WORD"]) };
            foreach (var item in required)
            {
                var entry = selected.GetValueOrDefault(station.Id)?.FirstOrDefault(e => e.Index == item.Index && e.SubIndex == 0 && e.IsInput == item.Input && e.BitLength == item.Width && item.Types.Contains(e.Type.ToUpperInvariant()));
                if (entry is null) Error($"缺少标准 CiA402 PDO 0x{item.Index:X4}:0 或方向、位宽、数据类型不符。");
                pdo[item.Name] = EntryKey(station.Name, item.Index, 0);
            }
            foreach (var item in new[] { (Name: "positionDemand", Index: (ushort)0x6062), (Name: "followingError", Index: (ushort)0x60F4) })
            {
                var entry = selected.GetValueOrDefault(station.Id)?.FirstOrDefault(e => e.Index == item.Index && e.SubIndex == 0 && e.IsInput && e.BitLength == 32 && e.Type.ToUpperInvariant() is "DINT" or "INT32");
                if (entry is not null) pdo[item.Name] = EntryKey(station.Name, item.Index, 0);
                else if (runtime.DriveDemandProtection == true || project.Hardware.Runtime?.AxisTrace?.Enabled == true) Error($"辅助保护缺少 0x{item.Index:X4}:0 的 32 位有符号输入 PDO。");
            }
            if (invalid) continue;
            result.Add(new { name = axis.ControllerAxis, slavePosition = Array.IndexOf(stations.ToArray(), station), drive = runtime.MotorModel, profile = "CiA402", mode = "Csp", modeOfOperation = 8, positionUnit = axis.Unit, pdoEntries = pdo,
                mechanics = new { commissioningRequired = !runtime.MechanicsConfirmed, motorModel = runtime.MotorModel, encoderCountsPerMotorRevolution = mechanics.FeedbackCountsPerMotorRevolution, motorTurnsPerOutputTurn = mechanics.MotorRevolutions / mechanics.LoadRevolutions, engineeringUnitsPerOutputTurn = axis.Unit switch { "rev" => 1.0, "deg" => 360.0, _ => mechanics.TravelPerLoadRevolution }, direction = mechanics.ReverseDirection ? -1 : 1 },
                softLimitNegative = axis.MinPosition, softLimitPositive = axis.MaxPosition, maximumVelocity = axis.MaximumVelocity, maximumAcceleration = axis.MaximumAcceleration, maximumDeceleration = axis.MaximumDeceleration, maximumJerk = axis.MaximumJerk,
                driveDemandProtection = runtime.DriveDemandProtection, driveErrorOnly = runtime.DriveErrorOnly, demandDeliveryEnforced = runtime.DemandDeliveryEnforced, demandDeliveryTolerance = runtime.DemandDeliveryTolerance,
                velocityFeedbackRawPerUnitPerSecond = runtime.VelocityFeedbackRawPerUnitPerSecond, velocityOffsetRawPerUnitPerSecond = runtime.VelocityOffsetRawPerUnitPerSecond, velocityFeedforwardGain = runtime.VelocityFeedforwardGain,
                followingErrorRawPerUnit = runtime.FollowingErrorRawPerUnit, positionDemandRawPerUnit = runtime.PositionDemandRawPerUnit,
                homing, motionCompletion = completion, motionSafety = new { commissioningRequired = !runtime.SafetyConfirmed, maximumActualVelocity = runtime.MaximumActualVelocity, maximumFollowingError = runtime.MaximumFollowingError, followingErrorTimeoutMilliseconds = runtime.FollowingErrorTimeoutMilliseconds } });
        }
        return result;
    }
    public static bool RequiresMotionMigration(StProject project) => project.Hardware.Runtime?.AxisTrace is not null || project.Hardware.Runtime?.CycleTiming is not null || project.Hardware.Stations?.Any(s => s.Runtime?.DcSync0ShiftNanoseconds is not null) == true || project.Hardware.Axes.Any(a => a.MaximumJerk is not null || a.Runtime is { } r && (r.DriveDemandProtection is not null || r.DriveErrorOnly is not null || r.DemandDeliveryEnforced is not null || r.DemandDeliveryTolerance is not null || r.VelocityFeedbackRawPerUnitPerSecond is not null || r.VelocityOffsetRawPerUnitPerSecond is not null || r.VelocityFeedforwardGain is not null || r.FollowingErrorRawPerUnit is not null || r.PositionDemandRawPerUnit is not null || r.DriveFollowingErrorWindowRaw is not null));
    private static bool Positive(double n) => double.IsFinite(n) && n > 0;
}

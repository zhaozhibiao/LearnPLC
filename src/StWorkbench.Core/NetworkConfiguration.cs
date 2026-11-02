using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace StWorkbench.Core;

public sealed record NetworkConfiguration(string ManagementHost, string SshUser, string RuntimeEndpoint, string EtherCatInterface);
public sealed record EtherCatStation(Guid Id, string Name, Guid DeviceId, ushort AliasAddress, IReadOnlyList<PdoEntry> EnabledEntries)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public EtherCatStationRuntimeSettings? Runtime { get; init; }
}
public sealed record HardwareConfigurationDocument(HardwareConfiguration Hardware)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CommunicationConfiguration? Communication { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public string State { get; init; } = "offline-engineering";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NetworkConfiguration? Network { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MotionCycleConfiguration? MotionCycle { get; init; }
}

public static class NetworkTopologyValidator
{
    public static IReadOnlyList<Diagnostic> Validate(StProject project)
    {
        var errors = new List<Diagnostic>();
        void Error(string message) => errors.Add(new("hardware-configuration", 1, 1, "error", message));
        if (project.Network is { } network)
        {
            if (!IsHost(network.ManagementHost)) Error("管理主机必须是有效 IP 地址或 DNS 主机名");
            if (network.SshUser is null || !Regex.IsMatch(network.SshUser, @"^[A-Za-z_][A-Za-z0-9_.-]{0,63}\z")) Error("SSH 用户名无效");
            if (network.RuntimeEndpoint is null || network.RuntimeEndpoint.Length > 0 && !IsOrigin(network.RuntimeEndpoint)) Error("Runtime 地址必须是无凭据、路径或查询的 HTTPS 地址");
            if (network.EtherCatInterface is null || network.EtherCatInterface.Length > 0 && (!Regex.IsMatch(network.EtherCatInterface, @"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,14}\z") || network.EtherCatInterface is "." or "..")) Error("EtherCAT 网卡名必须是 1–15 个安全 ASCII 字符");
        }
        var stations = project.Hardware.Stations ?? [];
        if (stations.Count > 256) Error("EtherCAT 从站数量不能超过 256");
        var ids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var aliases = new HashSet<ushort>();
        foreach (var station in stations)
        {
            if (station is null) { Error("EtherCAT 从站不能为空"); continue; }
            if (station.Id == Guid.Empty || !ids.Add(station.Id)) Error("EtherCAT 从站 ID 为空或重复");
            if (station.Name is null || !Regex.IsMatch(station.Name, @"^[A-Za-z_][A-Za-z0-9_]{0,63}\z") || !names.Add(station.Name)) Error("EtherCAT 从站名称无效或重复");
            if (station.AliasAddress != 0 && !aliases.Add(station.AliasAddress)) Error("EtherCAT 从站别名地址重复");
            var device = project.Hardware.Devices.FirstOrDefault(d => d.Id == station.DeviceId);
            if (device is null) Error("EtherCAT 从站引用的设备目录不存在");
            if (station.EnabledEntries is null) { Error("EtherCAT 从站 PDO 集合不能为空"); continue; }
            var keys = new HashSet<(bool, ushort, byte)>();
            foreach (var entry in station.EnabledEntries)
            {
                if (entry is null) { Error("EtherCAT 从站 PDO 项不能为空"); continue; }
                if (!keys.Add((entry.IsInput, entry.Index, entry.SubIndex))) Error("EtherCAT 从站启用的 PDO 项重复");
                if (device is not null && !device.Entries.Contains(entry)) Error("EtherCAT 从站 PDO 项与设备目录不符");
            }
        }
        foreach (var io in project.Hardware.Io)
        {
            if (io.StationId is null && io.PdoIndex is null && io.PdoSubIndex is null) continue;
            if (io.StationId is null || io.PdoIndex is null || io.PdoSubIndex is null) { Error("IO 从站/PDO 引用必须完整"); continue; }
            var station = stations.FirstOrDefault(s => s is not null && s.Id == io.StationId);
            var entry = station?.EnabledEntries?.FirstOrDefault(e => e is not null && e.Index == io.PdoIndex && e.SubIndex == io.PdoSubIndex && e.IsInput == io.IsInput);
            if (entry is null || !Compatible(io.Type, entry)) Error("IO 引用的启用 PDO 不存在、方向或类型位宽不符");
        }
        foreach (var axis in project.Hardware.Axes)
            if (axis.StationId is { } stationId && !ids.Contains(stationId)) Error("轴引用的 EtherCAT 从站不存在");
        return errors.AsReadOnly();
    }
    private static bool IsHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host.Any(c => c > 127 || char.IsWhiteSpace(c)) || host.Contains('%')) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        if (Regex.IsMatch(host, @"^[0-9.]+\z")) return false;
        return host.Split('.').All(label => Regex.IsMatch(label, @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\z"));
    }
    private static bool IsOrigin(string value) => Regex.IsMatch(value, @"^https://[^/?#\\]+/?\z", RegexOptions.IgnoreCase) && value == value.Trim() && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && IsHost(uri.Host) && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && !value.Contains('\\') && !value.Any(char.IsWhiteSpace);
    private static bool Compatible(string type, PdoEntry entry)
    {
        var normalized = type.ToUpperInvariant();
        if (entry.Type is null) return false;
        var pdo = entry.Type.ToUpperInvariant();
        var width = normalized switch { "BOOL" => 1, "BYTE" => 8, "WORD" or "INT" => 16, "DWORD" or "DINT" or "REAL" => 32, "LREAL" => 64, _ => 0 };
        var compatible = normalized switch
        {
            "BOOL" => pdo is "BOOL" or "BOOLEAN",
            "BYTE" => pdo is "BYTE" or "USINT" or "UINT8",
            "WORD" => pdo is "WORD" or "UINT" or "UINT16",
            "DWORD" => pdo is "DWORD" or "UDINT" or "UINT32",
            "INT" => pdo is "INT" or "INT16",
            "DINT" => pdo is "DINT" or "INT32",
            "REAL" => pdo is "REAL" or "REAL32",
            "LREAL" => pdo is "LREAL" or "REAL64",
            _ => false
        };
        return compatible && width == entry.BitLength;
    }
}

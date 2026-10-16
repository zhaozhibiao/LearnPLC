using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StWorkbench.Core;

public sealed record CommunicationConfiguration
{
    public CommunicationChannel[] Channels { get; init; } = [];
}
public sealed record CommunicationChannel
{
    public string Name { get; init; } = "Channel1";
    public string Protocol { get; init; } = "ModbusTcp";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 502;
    public string Device { get; init; } = "/dev/ttyUSB0";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "Even";
    public int StopBits { get; init; } = 1;
    public string Physical { get; init; } = "RS232";
    public string Direction { get; init; } = "Automatic";
    public int BeforeSendMicroseconds { get; init; }
    public int AfterSendMicroseconds { get; init; }
    public int TimeoutMilliseconds { get; init; } = 1000;
    public string Framing { get; init; } = "Raw";
    public string DelimiterHex { get; init; } = "0D0A";
    public int FixedLength { get; init; } = 1;
    public int IdleMilliseconds { get; init; } = 20;
}
public static class CommunicationBinding
{
    public const string Interface = "workbench-communication-v1";
    public static readonly string[] Blocks = ["WB_MB_REQUEST", "WB_COMM_CLOSE", "WB_TCP_CONNECT", "WB_TCP_SEND", "WB_TCP_RECEIVE", "WB_TCP_CLOSE", "WB_SERIAL_SEND", "WB_SERIAL_RECEIVE"];
    public static bool IsUsed(IReadOnlyList<SourceDocument> sources) => sources.SelectMany(s => StLanguageService.Scan(s.Text)).Any(t => Blocks.Contains(t.Text, StringComparer.OrdinalIgnoreCase) || t.Text.Equals("COMM_REF", StringComparison.OrdinalIgnoreCase));
    private static bool IsLiteralIp(string? host)
    {
        if (host is null || host.Any(char.IsWhiteSpace) || host.Contains('%') || host.Contains('[') || host.Contains(']')) return false;
        static bool V4(string text) => Regex.IsMatch(text, @"^(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}\z") && text.Split('.').All(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture) <= 255);
        if (!host.Contains(':')) return V4(host);
        if (host.Contains('.') && !V4(host[(host.LastIndexOf(':') + 1)..])) return false;
        return IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
    }
    public static IReadOnlyList<Diagnostic> Validate(CommunicationConfiguration? configuration, IReadOnlyList<SourceDocument> sources, IReadOnlyList<PlcTask> tasks)
    {
        var errors = new List<Diagnostic>();
        void Error(string message) => errors.Add(new("communication", 1, 1, "error", message));
        if (configuration is null) { if (IsUsed(sources)) Error("通信功能块需要配置通信通道。"); return errors; }
        var channels = configuration.Channels;
        if (channels is null || channels.Length is < 1 or > 16 || channels.Any(c => c is null)) { Error("通信通道数量必须为 1–16，且不能包含空对象。"); return errors; }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var devices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in channels)
        {
            if (c.Name is null || c.Name.Length > 64 || !Regex.IsMatch(c.Name, @"^[A-Za-z_][A-Za-z0-9_]*\z") || !names.Add(c.Name)) Error("通信通道名称无效或重复。");
            if (c.Protocol is not "ModbusTcp" and not "ModbusRtu" and not "Serial" and not "Tcp") Error("仅支持 Modbus 主站、串口点对点及 TCP 客户端。");
            if (c.TimeoutMilliseconds is < 10 or > 10000) Error("通信超时必须为 10–10000 ms。");
            if (c.Protocol is "ModbusTcp" or "Tcp")
            {
                if (!IsLiteralIp(c.Host) || c.Port is < 1 or > 65535) Error("TCP 需要完整的固定 IPv4/IPv6 地址（不含区域标识）及 1–65535 端口。");
            }
            else
            {
                if (c.Device is null || !Regex.IsMatch(c.Device, @"^/dev/[A-Za-z0-9_./-]{1,128}\z") || c.Device.Split('/').Any(p => p is "." or "..") || !devices.Add(c.Device)) Error("串口必须使用唯一 /dev/ 设备路径。");
                if (!new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400 }.Contains(c.BaudRate) || c.DataBits is < 7 or > 8 || c.Parity is not "None" and not "Even" and not "Odd" || c.StopBits is < 1 or > 2) Error("串口格式或波特率不支持。");
                if (c.Protocol == "ModbusRtu" && (c.DataBits != 8 || c.Parity == "None" && c.StopBits != 2)) Error("Modbus RTU 使用 8 位；无校验时需要两位停止位。");
                if (c.Physical is not "RS232" and not "RS485" || c.Direction is not "Automatic" and not "KernelRts" || c.BeforeSendMicroseconds is < 0 or > 100000 || c.AfterSendMicroseconds is < 0 or > 100000) Error("串口方向及切换延时无效。");
                if (c.Physical == "RS232" && c.Direction == "KernelRts") Error("KernelRts 方向控制仅适用于 RS485。");
            }
            if (c.Protocol is "ModbusTcp" or "ModbusRtu" && c.Framing != "Raw") Error("Modbus 使用协议内置组帧，组帧选项必须为 Raw。");
            if (c.Framing is not "Raw" and not "Fixed" and not "Delimiter" and not "Length16" and not "Idle" || c.FixedLength is < 1 or > 512 || c.IdleMilliseconds is < 1 or > 1000 || c.DelimiterHex is null || !Regex.IsMatch(c.DelimiterHex, @"^(?:[A-Fa-f0-9]{2}){1,8}\z")) Error("组帧参数无效，单帧最大 512 字节。");
        }
        if (IsUsed(sources) && tasks.Any(t => t.Enabled && t.IntervalMilliseconds is < 1 or > 1000)) Error("通信扫描看门狗要求 PLC 周期为 1–1000 ms。");
        var symbols = new StLanguageService().Index(sources);
        var containers = symbols.Where(s => s.Kind is "FUNCTION_BLOCK" or "TYPE").Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stateful = Blocks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool HasState(string type) => stateful.Any(n => Regex.IsMatch(type, @"\b" + Regex.Escape(n) + @"\b", RegexOptions.IgnoreCase));
        bool changed;
        do { changed = false; foreach (var member in symbols.Where(s => s.Owner is not null && containers.Contains(s.Owner))) if (HasState(member.Type)) changed |= stateful.Add(member.Owner!); } while (changed);
        if (sources.SelectMany(s => StLanguageService.Scan(s.Text)).Any(t => t.Text.StartsWith("CB_", StringComparison.OrdinalIgnoreCase))) Error("CB_ 为通信扫描桥保留名称，不能由用户代码直接引用。");
        foreach (var symbol in symbols)
        {
            if (names.Any(n => symbol.Name.Equals("COMM_" + n, StringComparison.OrdinalIgnoreCase)) || symbol.Name.StartsWith("CB_", StringComparison.OrdinalIgnoreCase)) Error("通信库保留名称被用户声明遮蔽。");
            if (symbol.Kind is "FUNCTION" or "FUNCTION_BLOCK" or "TYPE" && (Blocks.Contains(symbol.Name, StringComparer.OrdinalIgnoreCase) || symbol.Name.Equals("COMM_REF", StringComparison.OrdinalIgnoreCase))) Error("通信库类型名称不能被重新定义。");
            if (symbol.Owner is { } owner && symbols.Any(s => s.Kind == "FUNCTION" && s.Name == owner) && HasState(symbol.Type)) Error("有状态通信功能块不能声明在函数局部。");
        }
        return errors;
    }
    public static string Constants(CommunicationConfiguration c) => "VAR_GLOBAL CONSTANT\n" + string.Join("\n", c.Channels.OrderBy(c => c.Name, StringComparer.Ordinal).Select((c, i) => $"    COMM_{c.Name} : COMM_REF := {i + 1};")) + "\nEND_VAR\n";
    public static string Manifest(CommunicationConfiguration c, int period) => JsonSerializer.Serialize(new { SchemaVersion = 1, InterfaceVersion = Interface, PlcIntervalMilliseconds = period, Channels = c.Channels.OrderBy(c => c.Name, StringComparer.Ordinal).Select((c, i) => new { Index = i + 1, Configuration = c }) });
}

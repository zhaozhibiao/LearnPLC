using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StWorkbench.Toolchain;

namespace StWorkbench.Mcp;

public sealed class ServerOptions
{
    public string WorkspaceRoot { get; init; } = "";
    public string RepositoryRoot { get; init; } = "";
    public string NodeExecutable { get; init; } = "node";
    public string CompilerDistribution { get; init; } = "";
    public DeviceOptions[] Devices { get; init; } = [];
    public static ServerOptions Load(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("MCP configuration exceeds 64 KiB.");
        var options = JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path), new JsonSerializerOptions(McpJson.Options)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Empty MCP configuration.");
        if (!Path.IsPathFullyQualified(options.WorkspaceRoot) || !Directory.Exists(options.WorkspaceRoot) ||
            !Path.IsPathFullyQualified(options.RepositoryRoot) || !File.Exists(Path.Combine(options.RepositoryRoot, "Directory.Build.props")))
            throw new InvalidDataException("Absolute existing WorkspaceRoot and RepositoryRoot are required.");
        if (options.Devices is null || options.Devices.Length > 16 || options.Devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != options.Devices.Length) throw new InvalidDataException("Invalid/duplicate device configuration.");
        foreach (var device in options.Devices) device.Validate();
        return options;
    }
    public string Distribution => string.IsNullOrWhiteSpace(CompilerDistribution) ? Path.Combine(RepositoryRoot, ".tools", "strucpp-0.7.0", "strucpp") : Path.GetFullPath(CompilerDistribution);
    public DeviceOptions Device(string id) => Devices.SingleOrDefault(d => d.Id == id) ?? throw new ArgumentException("Device is not configured by the operator.");
}
public sealed class DeviceOptions
{
    public string Id { get; init; } = "";
    public string Host { get; init; } = "";
    public string User { get; init; } = "";
    public int Port { get; init; } = 22;
    public string KnownHostsPath { get; init; } = "";
    public bool AllowPlcControl { get; init; }
    public bool AllowMotion { get; init; }
    public string Backend { get; init; } = "EcMotion";
    public string[] AllowedAxes { get; init; } = [];
    public string GatewayPath { get; init; } = "/home/a/st-workbench-mcp/st-motion-gateway";
    public string AxisManifestPath { get; init; } = "/home/a/st-workbench-mcp/axis-manifest.json";
    public string ControllerSocket { get; init; } = "/run/ecmotion/controller.sock";
    public SshTarget Target => new(Host, User, Port);
    public void Validate()
    {
        if (!Regex.IsMatch(Id, @"^[A-Za-z0-9_-]{1,64}\z") || !Regex.IsMatch(Host, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}\z") ||
            !Regex.IsMatch(User, @"^[A-Za-z_][A-Za-z0-9_-]{0,31}\z") || Port is < 1 or > 65535 ||
            !Path.IsPathFullyQualified(KnownHostsPath) || !File.Exists(KnownHostsPath)) throw new InvalidDataException("Invalid configured device identity/known-hosts file.");
        if (Backend is not "EcMotion" and not "EcMotionSimulator" || AllowedAxes is null || AllowedAxes.Length > 64 || AllowedAxes.Any(a => string.IsNullOrWhiteSpace(a))) throw new InvalidDataException("Invalid motion backend or axis allowlist.");
        foreach (var path in new[] { GatewayPath, AxisManifestPath, ControllerSocket })
            if (!Regex.IsMatch(path, @"^/[A-Za-z0-9_./-]{1,250}\z") || path.Split('/').Contains("..")) throw new InvalidDataException("Remote motion paths must be absolute simple Unix paths.");
        if (AllowMotion && AllowedAxes.Length == 0) throw new InvalidDataException("Motion control requires an explicit axis allowlist.");
    }
}

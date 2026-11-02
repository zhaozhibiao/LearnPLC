using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StWorkbench.Core;
using StWorkbench.Mcp;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.CurrentDirectory);
var baseline = args.Contains("--baseline");
var folder = Path.Combine(root, "examples", "standard-axis-turn");
var phase = baseline ? "baseline" : "final";
var audit = new List<object>();
var server = Path.Combine(root, "src", "StWorkbench.Mcp", "bin", "online-page-verify", "StWorkbench.Mcp.dll");
await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions {
    Name = "Engineering axis X turn sequence authoring", Command = "dotnet",
    Arguments = [server, "--config", Path.Combine(root, "artifacts", "mcp-server.json")] }));
async Task<JsonElement> Call(string name, Dictionary<string,object?> arguments)
{
    var result = await client.CallToolAsync(name, arguments);
    var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    var json = JsonSerializer.Deserialize<JsonElement>(text);
    audit.Add(new { tool = name, arguments, isError = result.IsError, response = json });
    await File.WriteAllTextAsync(Path.Combine(folder, "mcp-" + phase + "-evidence.json"), JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
    if (result.IsError == true) throw new InvalidOperationException(name + ": " + text);
    Console.WriteLine("MCP " + name + " OK");
    return json;
}
var toolList = await client.ListToolsAsync();
Console.WriteLine("Discovered " + toolList.Count + " MCP tools");
_ = await Call("workbench_info", new());
var reference = await Call("project_open", new() { ["projectPath"] = "examples/generic-ethercat/IS620N-Generic.stproj" });
var model = reference.GetProperty("Project").Deserialize<StProject>(McpJson.Options)!;
const string projectPath = "examples/standard-axis-turn/StandardAxis.stproj";
var exists = File.Exists(Path.Combine(root, projectPath));
var view = await Call(exists ? "project_open" : "project_create", exists
    ? new() { ["projectPath"] = projectPath }
    : new() { ["projectPath"] = projectPath, ["name"] = "工程轴 X 正转1.5圈－等待2秒－反转1圈" });
var session = view.GetProperty("SessionId").GetString()!;
string Revision() => view.GetProperty("Revision").GetString()!;
var project = view.GetProperty("Project").Deserialize<StProject>(McpJson.Options)!;
var hardware = model.Hardware;
hardware.Axes = hardware.Axes.Select(axis => axis with { Name = "X", MinPosition = -2, MaxPosition = 2 }).ToList();
view = await Call("configuration_update", new() { ["sessionId"] = session, ["expectedRevision"] = Revision(),
    ["hardware"] = hardware, ["network"] = model.Network,
    ["tasks"] = new[] { new PlcTask { ProgramId = project.Items.Single().Id, IntervalMilliseconds = 10 } } });
_ = await Call("st_instructions", new() { ["sessionId"] = session, ["search"] = "axis" });
view = await Call("source_write", new() { ["sessionId"] = session, ["expectedRevision"] = Revision(),
    ["itemId"] = project.Items.Single().Id, ["text"] = await File.ReadAllTextAsync(Path.Combine(folder, "Main.st")) });
var validation = await Call("project_validate", new() { ["sessionId"] = session });
if (!validation.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Engineering validation failed");
view = await Call("project_save", new() { ["sessionId"] = session, ["expectedRevision"] = Revision() });
var build = await Call("build_start", new() { ["sessionId"] = session, ["expectedRevision"] = Revision() });
var id = build.GetProperty("OperationId").GetString()!;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
while (build.GetProperty("State").GetString() is "Queued" or "Running")
{
    await Task.Delay(500, deadline.Token);
    build = await Call("build_status", new() { ["operationId"] = id });
}
if (!build.GetProperty("Success").GetBoolean()) throw new InvalidOperationException("ST compiler failed: " + build);
var hash = build.GetProperty("BuildHash").GetString()!;
var manifests = Directory.EnumerateFiles(Path.Combine(root, ".st-mcp-builds"), "build-manifest.json", SearchOption.AllDirectories);
var manifest = manifests.Single(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("BuildHash").GetString() == hash);
var output = Path.GetDirectoryName(manifest)!;
var package = Path.Combine(Directory.GetParent(Directory.GetParent(output)!.FullName)!.FullName, "program.zip");
var evidence = new { ProjectPath = Path.Combine(root, projectPath), OutputDirectory = output, PackagePath = package,
    BuildHash = hash, AuthoredThroughMcp = true, SavedThroughMcp = true, CompiledThroughMcp = true,
    Downloaded = false, Activated = false, PhysicalMotionTested = false, Phase = phase };
await File.WriteAllTextAsync(Path.Combine(folder, "build-" + phase + "-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(evidence));

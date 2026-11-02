using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StWorkbench.Core;
using StWorkbench.Mcp;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.CurrentDirectory);
var baseline = args.Contains("--baseline");
var folder = Path.Combine(root, "examples", "library-axis-turn");
var phase = baseline ? "baseline" : "final";
var audit = new List<object>();
var server = Path.Combine(root, "src", "StWorkbench.Mcp", "bin", "library-axis-verify", "StWorkbench.Mcp.dll");
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
const string projectPath = "examples/library-axis-turn/LibraryAxis.stproj";
var exists = File.Exists(Path.Combine(root, projectPath));
var view = await Call(exists ? "project_open" : "project_create", exists
    ? new() { ["projectPath"] = projectPath }
    : new() { ["projectPath"] = projectPath, ["name"] = "右侧指令库 · 工程轴 X 往返示例" });
var session = view.GetProperty("SessionId").GetString()!;
string Revision() => view.GetProperty("Revision").GetString()!;
var project = view.GetProperty("Project").Deserialize<StProject>(McpJson.Options)!;
var hardware = model.Hardware;
hardware.Axes = hardware.Axes.Select(axis => axis with {
    Name = "X", MinPosition = -2, MaxPosition = 2,
    MaximumVelocity = 1.0, MaximumAcceleration = 1.0, MaximumDeceleration = 1.0,
    Runtime = axis.Runtime is { } runtime ? runtime with { MaximumActualVelocity = 1.5 } : null
}).ToList();
view = await Call("configuration_update", new() { ["sessionId"] = session, ["expectedRevision"] = Revision(),
    ["hardware"] = hardware, ["network"] = model.Network,
    ["tasks"] = new[] { new PlcTask { ProgramId = project.Items.Single().Id, IntervalMilliseconds = 10 } } });
var itemId = project.Items.Single().Id;
view = await Call("source_write", new() { ["sessionId"] = session, ["expectedRevision"] = Revision(),
    ["itemId"] = itemId, ["text"] = "PROGRAM Main\nVAR\nEND_VAR\n\nEND_PROGRAM\n" });
var catalog = await Call("st_instructions", new() { ["sessionId"] = session });
var instructions = catalog.GetProperty("templates").EnumerateArray().Select(x => x.Deserialize<StInstruction>(McpJson.Options)!).ToArray();
var roles = new (string Role,string Id)[] {
    ("AxisStatus","Axis_X_MC_ReadAxisStatus"),("AxisPosition","Axis_X_MC_ReadActualPosition"),("AxisVelocity","Axis_X_MC_ReadActualVelocity"),
    ("AxisStop","Axis_X_MC_Stop"),("AxisPower","Axis_X_MC_Power"),("AxisReset","Axis_X_MC_Reset"),("AxisHome","Axis_X_MC_Home"),
    ("ForwardMove","Axis_X_MC_MoveAbsolute"),("ReverseMove","Axis_X_MC_MoveAbsolute"),
    ("Dwell","TON"),("CycleTimeout","TON"),("StopTimeout","TON"),("CycleTrigger","R_TRIG") };
var generated = new Dictionary<string,(string Prefix,string Call)>();
var proof = new List<object>();
string Source() => view.GetProperty("Sources")[0].GetProperty("Text").GetString()!;
foreach(var (role,instructionId) in roles) {
    var instruction = instructions.Single(x => x.Id == instructionId);
    var text = Source(); var offset = text.IndexOf("END_PROGRAM",StringComparison.Ordinal);
    var expected = StSnippetInsertion.Insert(text,offset,instruction);
    view = await Call("st_insert_instruction",new() { ["sessionId"] = session,["expectedRevision"] = Revision(),["itemId"] = itemId,["offset"] = offset,["instructionId"] = instructionId });
    if(Source()!=expected.Text)throw new Exception("MCP and desktop insertion differ: "+instructionId);
    var code=instruction.Code.Replace("{p}",expected.InstancePrefix);
    generated[role]=(expected.InstancePrefix,code);
    proof.Add(new { Role=role,InstructionId=instructionId,Instance=expected.InstancePrefix,CatalogCall=code,
        Declarations=instruction.Declarations.Select(x=>x.Replace("{p}",expected.InstancePrefix)).ToArray(),InsertedThroughMcp=true,MatchesDesktopInsertion=true });
}
var declarations=Source().Split("END_VAR",2)[0].Split("VAR\n",2)[1];
var source=await File.ReadAllTextAsync(Path.Combine(root,"examples","standard-axis-turn","Main.st"));
source=source.Replace("(* Standard engineering-axis FB instances. *)","(* Instances and calls inserted through the actual right-side library catalog. *)");
foreach(var role in roles.Where(x=>x.Role!="CycleTrigger")) {
    source=System.Text.RegularExpressions.Regex.Replace(source,@"(?m)^\s*"+role.Role+@"\s*:\s*\w+\s*;\r?\n","");
    var (prefix,code)=generated[role.Role];
    var inputs=role.Role switch {
        "AxisPower" => prefix+"Enable := PowerEnable;\n",
        "AxisStop" => prefix+"Execute := StopExecute OR (Step = 80);\n",
        "AxisReset" => prefix+"Execute := ResetExecute AND NOT CycleActive;\n",
        "AxisHome" => prefix+"Execute := HomeExecute AND NOT CycleActive AND AxisPower.Status AND NOT AxisPower.Busy AND NOT StopExecute;\n",
        "ForwardMove" => prefix+"Execute := Step = 10;\n"+prefix+"Position := ForwardTarget;\n"+prefix+"Velocity := AcceptedSpeed;\n",
        "ReverseMove" => prefix+"Execute := Step = 30;\n"+prefix+"Position := ReverseTarget;\n"+prefix+"Velocity := AcceptedSpeed;\n",
        "Dwell" => prefix+"Input := Step = 20;\n",
        "CycleTimeout" => prefix+"Input := CycleActive;\n",
        "StopTimeout" => prefix+"Input := Step = 80;\n",
        _ => ""
    };
    if(role.Role is "Dwell" or "StopTimeout")code=code.Replace("T#1s","T#2s");
    if(role.Role=="CycleTimeout")code=code.Replace("T#1s","T#300s");
    var pattern=@"(?ms)^"+role.Role+@"\(.*?;\r?\n";
    if(System.Text.RegularExpressions.Regex.Matches(source,pattern).Count!=1)throw new Exception("Missing baseline call: "+role.Role);
    source=System.Text.RegularExpressions.Regex.Replace(source,pattern,inputs+code+"\n");
}
var trigger=generated["CycleTrigger"];
source=source.Replace("CycleRise := CycleExecute AND NOT PreviousCycle;",trigger.Prefix+"Signal := CycleExecute;\n"+trigger.Call+"\nCycleRise := "+trigger.Prefix+".Q;");
source=System.Text.RegularExpressions.Regex.Replace(source,@"(?m)^\s*PreviousCycle\s*:\s*BOOL;\r?\n","");
source=System.Text.RegularExpressions.Regex.Replace(source,@"(?m)^PreviousCycle := CycleExecute;\r?\n","");
foreach(var role in roles.Where(x=>x.Role!="CycleTrigger"))source=System.Text.RegularExpressions.Regex.Replace(source,@"\b"+role.Role+@"\b",generated[role.Role].Prefix);
source=source.Replace("END_VAR",declarations+"END_VAR");
foreach(var role in roles.Where(x=>x.Id.StartsWith("Axis_",StringComparison.Ordinal)))
    if(!source.Contains(generated[role.Role].Call,StringComparison.Ordinal))throw new Exception("Final engineering-axis call differs from right library: "+role.Role);
view=await Call("source_write",new() { ["sessionId"] = session,["expectedRevision"] = Revision(),["itemId"] = itemId,["text"] = source });
await File.WriteAllTextAsync(Path.Combine(folder,"Main.st"),source);
await File.WriteAllTextAsync(Path.Combine(folder,"library-insertion-evidence.json"),JsonSerializer.Serialize(proof,new JsonSerializerOptions {WriteIndented=true}));
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
var manifest = manifests.Where(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("BuildHash").GetString() == hash)
    .Where(path => File.Exists(Path.Combine(Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName)!.FullName, "program.zip")))
    .OrderByDescending(File.GetLastWriteTimeUtc).First();
var output = Path.GetDirectoryName(manifest)!;
var package = Path.Combine(Directory.GetParent(Directory.GetParent(output)!.FullName)!.FullName, "program.zip");
var evidence = new { ProjectPath = Path.Combine(root, projectPath), OutputDirectory = output, PackagePath = package,
    BuildHash = hash, AuthoredThroughMcp = true, SavedThroughMcp = true, CompiledThroughMcp = true,
    Downloaded = false, Activated = false, PhysicalMotionTested = false, Phase = phase };
await File.WriteAllTextAsync(Path.Combine(folder, "build-" + phase + "-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(evidence));

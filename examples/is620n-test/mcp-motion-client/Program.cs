// MIT. Default is read-only. --execute-once issues real physical motion.
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.CurrentDirectory).Replace(Path.DirectorySeparatorChar, '/');
var execute = args.Contains("--execute-once", StringComparer.Ordinal);
var audit = new List<object>();
const string device = "is620n-test";
await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions {
    Name="IS620N physical commissioning", Command="dotnet",
    Arguments=[root+"/artifacts/mcp-server/StWorkbench.Mcp.dll", "--config", root+"/artifacts/mcp-is620n.json"] }));
async Task<JsonElement> Call(string tool, Dictionary<string,object?> arguments)
{
    var result = await client.CallToolAsync(tool, arguments);
    var json = JsonSerializer.Deserialize<JsonElement>(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b=>b.Text)));
    audit.Add(new { utc=DateTimeOffset.UtcNow, tool, arguments, isError=result.IsError==true, response=json });
    await File.WriteAllTextAsync(root+"/examples/is620n-test/mcp-motion-evidence.json", JsonSerializer.Serialize(audit, new JsonSerializerOptions {WriteIndented=true}));
    if (result.IsError==true || json.TryGetProperty("success",out var ok) && ok.ValueKind==JsonValueKind.False)
        throw new InvalidOperationException(tool+": "+json);
    return json;
}
Dictionary<string,object?> Device() => new() { ["deviceId"]=device };
var view=await Call("project_open", new(){["projectPath"]="examples/is620n-test/IS620N.stproj"});
var session=view.GetProperty("SessionId").GetString()!;
var revision=view.GetProperty("Revision").GetString()!;
var capabilities=await Call("motion_capabilities",Device());
Console.WriteLine(capabilities);
var before=await Call("motion_status",Device());
Console.WriteLine(before);
if (!execute) return;
var cap=capabilities.GetProperty("capabilities");
if (cap.GetProperty("Backend").GetString()!="EcMotion" || !cap.GetProperty("PhysicalBasicAxis").GetBoolean() || !cap.GetProperty("ControllerReady").GetBoolean())
    throw new InvalidOperationException("Physical controller proof unavailable; no commands issued.");
JsonElement Sample(JsonElement status) {
    var axes=status.GetProperty("axes");
    if (axes.GetArrayLength()!=1 || axes[0].GetProperty("axis").GetString()!="IS620N" || axes[0].GetProperty("unit").GetString()!="rev")
        throw new InvalidOperationException("Unexpected physical axis mapping/units.");
    return axes[0].GetProperty("sample");
}
async Task<JsonElement> WaitForRest(string state, bool homed=false) {
    var deadline=DateTimeOffset.UtcNow.AddSeconds(3);
    var consecutive=0;
    double minimum=double.PositiveInfinity, maximum=double.NegativeInfinity;
    long previousCycle=0;
    while(DateTimeOffset.UtcNow<deadline) {
        var status=await Call("motion_status",Device());
        var sample=Sample(status);
        var position=sample.GetProperty("actualPosition").GetDouble();
        var velocity=sample.GetProperty("actualVelocity").GetDouble();
        var cycle=status.GetProperty("sampleCycle").GetInt64();
        if (cycle<=previousCycle) throw new InvalidOperationException("Actual sample cycle did not advance.");
        previousCycle=cycle;
        if (sample.GetProperty("state").GetString()==state && Math.Abs(velocity)<=.001 && Math.Abs(position)<=1
            && (!homed || sample.GetProperty("homed").GetBoolean())) {
            minimum=Math.Min(minimum,position); maximum=Math.Max(maximum,position);
            if (maximum-minimum<=.00001 && ++consecutive>=5) return sample;
        } else { consecutive=0; minimum=double.PositiveInfinity; maximum=double.NegativeInfinity; }
        await Task.Delay(25);
    }
    throw new InvalidOperationException("No continuous stationary feedback window; no next command sent.");
}
_=await WaitForRest("Disabled");
var acquired=false;
async Task<JsonElement> Wait(JsonElement accepted) {
    var id=accepted.GetProperty("operation").GetProperty("operationId").GetString()!;
    var deadline=DateTimeOffset.UtcNow.AddSeconds(7);
    while (DateTimeOffset.UtcNow<deadline) {
        await Task.Delay(50);
        var result=await Call("motion_operation",new(){["deviceId"]=device,["operationId"]=id});
        var state=result.GetProperty("state").GetString();
        if (state=="Completed") return result;
        if (state is "Unknown" or "Failed" or "Aborted" or "Rejected") throw new InvalidOperationException("Motion terminal state: "+result);
    }
    throw new TimeoutException("Motion result not proven; never replay.");
}
async Task Action(string kind,double position=0,double velocity=0) {
    var plan=await Call("motion_prepare",new(){["deviceId"]=device,["sessionId"]=session,["expectedRevision"]=revision,
        ["axis"]="IS620N",["kind"]=kind,["position"]=position,["velocity"]=velocity});
    Console.WriteLine($"Reviewed {kind}: axis IS620N, rev, target {position}, speed {velocity}");
    var accepted=await Call("motion_execute",new(){["planId"]=plan.GetProperty("PlanId").GetString()});
    var completed=await Wait(accepted);
    Console.WriteLine(kind+" Completed "+completed.GetProperty("operation").GetProperty("sample"));
}
try {
    _=await Call("motion_acquire",new(){["deviceId"]=device,["sessionId"]=session,["expectedRevision"]=revision,["durationMilliseconds"]=1000});
    acquired=true;
    await Action("Enable");
    _=await WaitForRest("Standstill");
    await Action("Home");
    var homed=await WaitForRest("Standstill",true);
    var origin=homed.GetProperty("actualPosition").GetDouble();
    if (!homed.GetProperty("homed").GetBoolean() || Math.Abs(origin)>.0001) throw new InvalidOperationException("Temporary zero was not established.");
    await Action("MoveAbsolute",origin+.01,.01);
    var outward=Sample(await Call("motion_status",Device())).GetProperty("actualPosition").GetDouble();
    if (Math.Abs(outward-origin-.01)>.0001) throw new InvalidOperationException("Outward displacement not proven.");
    await Task.Delay(250);
    _=await WaitForRest("Standstill",true);
    await Action("MoveAbsolute",origin,.01);
    var returned=Sample(await Call("motion_status",Device()));
    if (Math.Abs(returned.GetProperty("actualPosition").GetDouble()-origin)>.0001)
        throw new InvalidOperationException("Powered return position not proven.");
    await Task.Delay(250);
    await Wait(await Call("motion_stop",new(){["deviceId"]=device,["axis"]="IS620N"}));
    await Action("Disable");
    var final=await WaitForRest("Disabled");
    // Positioning accuracy is proven while torque is maintained. A deenergized
    // unloaded rotor can settle into a detent; record it separately from motion.
    if (final.GetProperty("state").GetString()!="Disabled" || Math.Abs(final.GetProperty("actualVelocity").GetDouble())>.001)
        throw new InvalidOperationException("Return and disabled state not proven.");
    Console.WriteLine("Final unpowered position rev="+final.GetProperty("actualPosition").GetDouble());
    Console.WriteLine("MCP_PHYSICAL_ROUNDTRIP_PASS distance_rev=0.01 velocity_rev_s=0.01");
} finally {
    if (acquired) {
        try { _=await Call("motion_stop",new(){["deviceId"]=device,["axis"]="IS620N"}); } catch(Exception e) { Console.Error.WriteLine("Stop result: "+e.Message); }
        try { _=await Call("motion_release",Device()); } catch(Exception e) { Console.Error.WriteLine("Release result: "+e.Message); }
    }
    try { _=await Call("motion_close",Device()); } catch(Exception e) { Console.Error.WriteLine("Close result: "+e.Message); }
}

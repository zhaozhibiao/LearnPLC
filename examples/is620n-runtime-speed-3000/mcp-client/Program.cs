using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StWorkbench.Core;
using StWorkbench.Mcp;

static string FindRepoRoot() {
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "artifacts", "mcp-server.json"))) d = d.Parent;
    return (d?.FullName ?? Environment.CurrentDirectory).Replace(Path.DirectorySeparatorChar, '/');
}
var root = args.Length > 0 ? Path.GetFullPath(args[0]).Replace(Path.DirectorySeparatorChar, '/') : FindRepoRoot();
var folder = root + "/examples/is620n-runtime-speed-3000";
var audit = new List<object>();

var mcpDll = root + "/artifacts/mcp-server/StWorkbench.Mcp.dll";
var configPath = root + "/artifacts/mcp-server.json";

if (!File.Exists(mcpDll)) throw new FileNotFoundException("MCP server DLL not found: " + mcpDll);
if (!File.Exists(configPath)) throw new FileNotFoundException("MCP config file not found: " + configPath);

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "IS620N Native Axis MCP Test Client",
    Command = "dotnet",
    Arguments = [mcpDll, "--config", configPath]
});

await using var client = await McpClient.CreateAsync(transport);

async Task<JsonElement> Call(string tool, Dictionary<string, object?> arguments)
{
    var result = await client.CallToolAsync(tool, arguments);
    var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    audit.Add(new
    {
        timestamp = DateTime.UtcNow.ToString("o"),
        tool,
        arguments,
        status = result.IsError == true ? "error" : "success",
        response = JsonSerializer.Deserialize<JsonElement>(text)
    });
    if (result.IsError == true) throw new InvalidOperationException($"MCP tool '{tool}' failed: {text}");
    Console.WriteLine($"[MCP] {tool} -> SUCCESS");
    return JsonSerializer.Deserialize<JsonElement>(text);
}

// 1. Tool discovery & Info
var tools = await client.ListToolsAsync();
Console.WriteLine($"[MCP] Discovered {tools.Count} tools on StWorkbench.Mcp server");
var info = await Call("workbench_info", new());

// 2. Open target project
const string projectPath = "examples/is620n-runtime-speed-3000/IS620N-Runtime-Migrated.stproj";
var view = await Call("project_open", new() { ["projectPath"] = projectPath });
var session = view.GetProperty("SessionId").GetString()!;
var revision = view.GetProperty("Revision").GetString()!;
var sources = view.GetProperty("Sources");
var mainItem = sources[0].GetProperty("ItemId").GetGuid();

Console.WriteLine($"[MCP] Project opened: SessionId={session}, Revision={revision}, MainItemId={mainItem}");

// 3. Define the closed-loop ST test program using configured axis AXIS_X native instructions (with Jerk & Acc/Dec)
var stSourceText = """
(* MIT. Standard MC_* native closed-loop test sequence on configured axis AXIS_X *)
PROGRAM Main
VAR
    PowerEnable : BOOL := FALSE;
    StartExecute : BOOL := FALSE;
    JogExecute : BOOL := FALSE;
    StopExecute : BOOL := FALSE;
    ResetExecute : BOOL := FALSE;

    (* Target parameters within configured limits: MaxVel 50, MaxAcc 5, MaxDec 5, MaxJerk 20 *)
    ForwardTurns : LREAL := 2.0;
    ReverseTurns : LREAL := 2.0;
    Speed : LREAL := 1.0;
    Acc : LREAL := 2.0;
    Dec : LREAL := 2.0;
    JerkVal : LREAL := 10.0;
    JogSpeed : LREAL := 0.5;

    Step : UINT := 0;
    StartPosition : LREAL := 0.0;
    ErrorID : UDINT := 0;
    Done : BOOL := FALSE;
    Cancelled : BOOL := FALSE;

    (* Native Axis Function Block Instances *)
    Power : MC_Power;
    Home : MC_Home;
    Reset : MC_Reset;
    Status : MC_ReadStatus;
    Position : MC_ReadActualPosition;
    MoveFwd : MC_MoveAbsolute;
    MoveRev : MC_MoveAbsolute;
    Jog : MC_MoveVelocity;
    Stop : MC_Stop;

    (* Timers & Triggers *)
    Dwell : TON;
    JogTimer : TON;
    StartEdge : R_TRIG;
END_VAR

(* Continuous state & position feedback read on AXIS_X every scan *)
Status(Axis := AXIS_X, Enable := TRUE);
Position(Axis := AXIS_X, Enable := TRUE);
StartEdge(CLK := StartExecute);

(* Commands called cyclically with parameter bindings *)
Power(Axis := AXIS_X, Enable := PowerEnable);
Reset(Axis := AXIS_X, Execute := ResetExecute);
Home(Axis := AXIS_X, Execute := (Step = 10));

MoveFwd(Axis := AXIS_X, Execute := (Step = 20),
    Position := StartPosition + ForwardTurns,
    Velocity := Speed, Acceleration := Acc, Deceleration := Dec, Jerk := JerkVal);

Dwell(IN := (Step = 30), PT := T#1s);

MoveRev(Axis := AXIS_X, Execute := (Step = 40),
    Position := StartPosition + ForwardTurns - ReverseTurns,
    Velocity := Speed, Acceleration := Acc, Deceleration := Dec, Jerk := JerkVal);

Jog(Axis := AXIS_X, Execute := (Step = 50) OR JogExecute,
    Velocity := JogSpeed, Acceleration := Acc, Deceleration := Dec, Jerk := JerkVal);

JogTimer(IN := (Step = 50), PT := T#1500ms);

Stop(Axis := AXIS_X, Execute := StopExecute OR (Step = 60),
    Deceleration := Dec, Jerk := JerkVal);

(* Sequence State Machine *)
IF StopExecute AND (Step > 0) AND (Step < 100) THEN
    Cancelled := TRUE;
    Step := 60;
END_IF;

CASE Step OF
0: (* Idle wait for Start trigger when powered and stand still *)
    IF StartEdge.Q AND Power.Status AND Status.Standstill THEN
        StartPosition := Position.Position;
        ErrorID := 0;
        Cancelled := FALSE;
        Done := FALSE;
        IF Status.Homed THEN
            Step := 20; (* If already homed, proceed directly to forward move *)
        ELSE
            Step := 10; (* Otherwise perform homing *)
        END_IF;
    END_IF;

10: (* Homing *)
    IF Home.Done THEN
        StartPosition := Position.Position;
        Step := 20;
    ELSIF Home.Error THEN
        ErrorID := Home.ErrorID;
        Step := 900;
    ELSIF Home.CommandAborted THEN
        Cancelled := TRUE;
        Step := 100;
    END_IF;

20: (* Forward Absolute Move with Jerk *)
    IF MoveFwd.Done THEN
        Step := 30;
    ELSIF MoveFwd.Error THEN
        ErrorID := MoveFwd.ErrorID;
        Step := 900;
    ELSIF MoveFwd.CommandAborted THEN
        Cancelled := TRUE;
        Step := 100;
    END_IF;

30: (* Dwell 1s *)
    IF Dwell.Q THEN
        Step := 40;
    END_IF;

40: (* Reverse Absolute Move back to start with Jerk *)
    IF MoveRev.Done THEN
        Step := 50;
    ELSIF MoveRev.Error THEN
        ErrorID := MoveRev.ErrorID;
        Step := 900;
    ELSIF MoveRev.CommandAborted THEN
        Cancelled := TRUE;
        Step := 100;
    END_IF;

50: (* Jog / Velocity motion for 1.5s *)
    IF JogTimer.Q THEN
        Step := 60; (* Trigger controlled stop *)
    ELSIF Jog.Error THEN
        ErrorID := Jog.ErrorID;
        Step := 900;
    ELSIF Jog.CommandAborted THEN
        Cancelled := TRUE;
        Step := 100;
    END_IF;

60: (* Controlled Stop *)
    IF Stop.Done THEN
        Step := 100;
    ELSIF Stop.Error THEN
        IF ErrorID = 0 THEN ErrorID := Stop.ErrorID; END_IF;
        Step := 900;
    END_IF;

100: (* Sequence completed *)
    Done := NOT Cancelled;
    IF NOT StartExecute AND NOT StopExecute THEN
        Step := 0;
    END_IF;

900: (* Error state, wait for Reset *)
    IF Reset.Done AND NOT StartExecute AND NOT StopExecute THEN
        ErrorID := 0;
        Step := 0;
    END_IF;
END_CASE;
END_PROGRAM
""";

// 4. Write source via MCP
view = await Call("source_write", new()
{
    ["sessionId"] = session,
    ["expectedRevision"] = revision,
    ["itemId"] = mainItem,
    ["text"] = stSourceText
});
revision = view.GetProperty("Revision").GetString()!;

// 5. Validate draft via MCP
var validation = await Call("project_validate", new() { ["sessionId"] = session });
var isValid = validation.GetProperty("success").GetBoolean();
Console.WriteLine($"[MCP] Validation success: {isValid}");
if (!isValid)
{
    var diag = validation.GetProperty("diagnostics").ToString();
    throw new InvalidOperationException($"Engineering validation failed: {diag}");
}

// 6. Save project draft via MCP
view = await Call("project_save", new()
{
    ["sessionId"] = session,
    ["expectedRevision"] = revision
});
revision = view.GetProperty("Revision").GetString()!;
Console.WriteLine($"[MCP] Project saved, new revision: {revision}");

// 7. Start real build via MCP
var buildStart = await Call("build_start", new()
{
    ["sessionId"] = session,
    ["expectedRevision"] = revision
});
var operationId = buildStart.GetProperty("OperationId").GetString()!;
Console.WriteLine($"[MCP] Build started: OperationId={operationId}");

// 8. Poll build status until done
JsonElement buildStatus;
do
{
    await Task.Delay(300);
    buildStatus = await Call("build_status", new() { ["operationId"] = operationId });
} while (buildStatus.GetProperty("State").GetString() is "Queued" or "Running");

var buildSuccess = buildStatus.GetProperty("Success").GetBoolean();
Console.WriteLine($"[MCP] Build result: Success={buildSuccess}");
if (!buildSuccess)
{
    var log = buildStatus.TryGetProperty("Log", out var l) ? l.GetString() : "No log";
    throw new InvalidOperationException($"Real compiler build failed: {log}");
}

var buildHash = buildStatus.GetProperty("BuildHash").GetString()!;
var outDir = buildStatus.TryGetProperty("OutputDirectory", out var od) ? od.GetString() : "";

// 9. Record Evidence
var evidence = new
{
    RecordedAtUtc = DateTime.UtcNow.ToString("o"),
    ProjectPath = projectPath,
    BuildHash = buildHash,
    OutputDirectory = outDir,
    SavedThroughMcp = true,
    ValidatedThroughMcp = true,
    CompiledThroughMcp = true,
    AuditTrail = audit
};

var evidencePath = folder + "/mcp-evidence.json";
await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"[MCP] Evidence written to: {evidencePath}");
Console.WriteLine($"================================================================================");
Console.WriteLine($"MCP AUTHORING & REAL COMPILATION SUCCEEDED!");
Console.WriteLine($"BuildHash: {buildHash}");
Console.WriteLine($"================================================================================");

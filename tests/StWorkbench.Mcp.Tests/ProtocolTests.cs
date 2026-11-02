using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StWorkbench.Mcp;
using StWorkbench.Core;

internal static class ProtocolTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "st-mcp-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = FindRepository();
            var configPath = Path.Combine(root, "server.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new ServerOptions { WorkspaceRoot = root, RepositoryRoot = repository, NodeExecutable = OperatingSystem.IsWindows() ? "C:/Program Files/nodejs/node.exe" : "node" }));
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ST Workbench integration", Command = "dotnet",
                Arguments = [Path.Combine(AppContext.BaseDirectory, "StWorkbench.Mcp.dll"), "--config", configPath]
            });
            await using var client = await McpClient.CreateAsync(transport);
            var tools = await client.ListToolsAsync();
            WorkspaceTests.Check(tools.Count >= 35 && tools.Any(t => t.Name == "motion_execute") && tools.Any(t => t.Name == "configuration_update"), "real MCP tool discovery");
            var info = await Call(client, "workbench_info", new());
            WorkspaceTests.Check(info.GetProperty("transport").GetString() == "stdio", "server identity");
            _ = await Call(client, "workbench_schema", new());
            var project = await Call(client, "project_create", new() { ["name"] = "McpRoundtrip", ["projectPath"] = "Demo/Main.stproj" });
            var session = project.GetProperty("SessionId").GetString()!;
            var revision = project.GetProperty("Revision").GetString()!;
            var item = project.GetProperty("Sources")[0].GetProperty("ItemId").GetGuid();
            project = await Call(client, "source_write", new() { ["sessionId"] = session, ["expectedRevision"] = revision, ["itemId"] = item,
                ["text"] = "PROGRAM Main\nVAR\ncounter : DINT := 0;\nEND_VAR\ncounter := counter + 1;\nEND_PROGRAM\n" });
            var stale = await client.CallToolAsync("source_write", new Dictionary<string, object?> { ["sessionId"] = session, ["expectedRevision"] = revision, ["itemId"] = item, ["text"] = "bad" });
            WorkspaceTests.Check(stale.IsError == true, "revision conflict is protocol tool error");
            revision = project.GetProperty("Revision").GetString()!;
            var saved = await Call(client, "project_save", new() { ["sessionId"] = session, ["expectedRevision"] = revision });
            WorkspaceTests.Check(!saved.GetProperty("HasUnsavedChanges").GetBoolean(), "MCP saves actual project");
            var loaded = await new ProjectStore().LoadAsync(Path.Combine(root, "Demo/Main.stproj"));
            WorkspaceTests.Check(loaded.Sources.Single().Text.Contains("counter := counter + 1"), "MCP file compatible with desktop store");
            WorkspaceTests.Check(tools.Any(t => t.Name == "communication_configure") && tools.Any(t => t.Name == "control_communication_catalog"), "communication and control tool discovery");
            project = await Call(client, "communication_configure", new() { ["sessionId"] = session, ["expectedRevision"] = revision,
                ["communication"] = new CommunicationConfiguration { Channels = [new() { Name = "Bus", Host = "127.0.0.1", Port = 15020 }] } });
            revision = project.GetProperty("Revision").GetString()!;
            var catalog = await Call(client, "control_communication_catalog", new() { ["sessionId"] = session });
            WorkspaceTests.Check(catalog.ToString().Contains("WB_PID") && catalog.ToString().Contains("COMM_Bus"), "actual ST interfaces and channel constants");
            foreach (var id in new[] { "WB_PID", "Comm_Bus_WB_MB_REQUEST" })
            {
                var currentText = project.GetProperty("Sources")[0].GetProperty("Text").GetString()!;
                project = await Call(client, "st_insert_instruction", new() { ["sessionId"] = session, ["expectedRevision"] = revision,
                    ["itemId"] = item, ["offset"] = currentText.IndexOf("END_PROGRAM", StringComparison.Ordinal), ["instructionId"] = id });
                revision = project.GetProperty("Revision").GetString()!;
            }
            var featureBuild = await Call(client, "build_start", new() { ["sessionId"] = session, ["expectedRevision"] = revision });
            featureBuild = await WaitBuild(client, featureBuild.GetProperty("OperationId").GetString()!);
            WorkspaceTests.Check(featureBuild.GetProperty("Success").GetBoolean(), "MCP inserted real control and communication templates compile: " + featureBuild.GetProperty("Log").GetString());
            project = await Call(client, "source_write", new() { ["sessionId"] = session, ["expectedRevision"] = revision, ["itemId"] = item, ["text"] = loaded.Sources.Single().Text });
            revision = project.GetProperty("Revision").GetString()!;
            project = await Call(client, "communication_configure", new() { ["sessionId"] = session, ["expectedRevision"] = revision });
            revision = project.GetProperty("Revision").GetString()!;
            _ = await Call(client, "project_save", new() { ["sessionId"] = session, ["expectedRevision"] = revision });
            var unknown = await client.CallToolAsync("device_status", new Dictionary<string, object?> { ["deviceId"] = "unconfigured" });
            WorkspaceTests.Check(unknown.IsError == true, "unknown device rejected without network");
            var build = await Call(client, "build_start", new() { ["sessionId"] = session, ["expectedRevision"] = revision });
            var buildId = build.GetProperty("OperationId").GetString()!;
            build = await WaitBuild(client, buildId);
            WorkspaceTests.Check(build.GetProperty("Success").GetBoolean(), "real standalone compiler MCP package: " + build.GetProperty("Log").GetString());
            project = await Call(client, "source_write", new() { ["sessionId"] = session, ["expectedRevision"] = revision, ["itemId"] = item,
                ["text"] = "PROGRAM Main\nVAR\ncounter : DINT := 0;\nEND_VAR\ncounter := UnknownSymbol + 1;\nEND_PROGRAM\n" });
            revision = project.GetProperty("Revision").GetString()!;
            build = await Call(client, "build_start", new() { ["sessionId"] = session, ["expectedRevision"] = revision });
            build = await WaitBuild(client, build.GetProperty("OperationId").GetString()!);
            WorkspaceTests.Check(!build.GetProperty("Success").GetBoolean() && build.GetProperty("Diagnostics").GetArrayLength() > 0, "real compiler located diagnostics via MCP");
            // Exercise typed engineering inputs over the actual protocol, including axis/path JSON.
            project = await Call(client, "source_write", new() { ["sessionId"] = session, ["expectedRevision"] = revision, ["itemId"] = item,
                ["text"] = loaded.Sources.Single().Text });
            revision = project.GetProperty("Revision").GetString()!;
            var model = project.GetProperty("Project").Deserialize<StProject>(McpJson.Options)!;
            var hardware = new HardwareConfiguration { Axes = [new("X", "Axis1", "mm", -1000, 1000) { MaximumVelocity = 20, MaximumAcceleration = 100, MaximumDeceleration = 100, MaximumJerk = 1000 },
                new("Y", "Axis2", "mm", -1000, 1000) { MaximumVelocity = 20, MaximumAcceleration = 100, MaximumDeceleration = 100, MaximumJerk = 1000 }] };
            project = await Call(client, "configuration_update", new() { ["sessionId"] = session, ["expectedRevision"] = revision, ["hardware"] = hardware, ["tasks"] = model.Tasks });
            revision = project.GetProperty("Revision").GetString()!;
            var axisCatalog = await Call(client, "st_instructions", new() { ["sessionId"] = session, ["search"] = "Axis_X_" });
            WorkspaceTests.Check(axisCatalog.GetProperty("templates").GetArrayLength() == 10, "canonical axis templates and compatibility alias exposed through actual MCP");
            var motionSignatures = axisCatalog.GetProperty("motionSignatures").EnumerateArray().ToArray();
            foreach (var block in new[] { "MC_MoveAbsolute", "MC_MoveVelocity", "MC_Stop" })
            {
                foreach (var input in block == "MC_Stop" ? new[] { "Deceleration", "Jerk" } : new[] { "Acceleration", "Deceleration", "Jerk" })
                    WorkspaceTests.Check(motionSignatures.Any(s => s.GetProperty("Owner").GetString() == block && s.GetProperty("Name").GetString() == input && s.GetProperty("Kind").GetString() == "VAR_INPUT"), "MCP advertises actual v2 public input " + block + "." + input);
                var signature = await Call(client, "st_symbols", new() { ["sessionId"] = session, ["signatureName"] = block });
                WorkspaceTests.Check(signature.ToString().Contains("Deceleration", StringComparison.Ordinal) && signature.ToString().Contains("Jerk", StringComparison.Ordinal), "MCP signature completion includes verified dynamic inputs");
            }
            WorkspaceTests.Check(!motionSignatures.Any(s => s.GetProperty("Owner").GetString() == "MC_Stop" && s.GetProperty("Name").GetString() == "Acceleration"), "MCP Stop does not advertise acceleration");
            var axisTemplateIds = axisCatalog.GetProperty("templates").EnumerateArray().Select(t => t.GetProperty("Id").GetString()).ToArray();
            WorkspaceTests.Check(axisTemplateIds.Contains("Axis_X_MC_ReadStatus") && axisTemplateIds.Contains("Axis_X_MC_ReadAxisStatus"), "MCP lists canonical and legacy status template IDs");
            foreach (var id in new[] { "Axis_X_MC_Power", "Axis_X_MC_MoveAbsolute", "Axis_X_MC_MoveAbsolute", "Axis_X_MC_ReadStatus", "Axis_X_MC_ReadAxisStatus" })
            {
                var text = project.GetProperty("Sources")[0].GetProperty("Text").GetString()!;
                project = await Call(client, "st_insert_instruction", new() { ["sessionId"] = session, ["expectedRevision"] = revision,
                    ["itemId"] = item, ["offset"] = text.IndexOf("END_PROGRAM", StringComparison.Ordinal), ["instructionId"] = id });
                revision = project.GetProperty("Revision").GetString()!;
            }
            var axisText = project.GetProperty("Sources")[0].GetProperty("Text").GetString()!;
            WorkspaceTests.Check(axisText.Contains("SnippetAxis_X_MC_MoveAbsolute2(Axis := AXIS_X") && axisText.Contains("SnippetAxis_X_MC_Power1Enable : BOOL := FALSE;"), "MCP uses actual desktop axis declaration/call naming and unique instances");
            WorkspaceTests.Check(axisText.Contains("SnippetAxis_X_MC_ReadStatus1 : MC_ReadStatus;") && axisText.Contains("SnippetAxis_X_MC_ReadAxisStatus1 : MC_ReadAxisStatus;") && axisText.Contains("SnippetAxis_X_MC_ReadStatus1(Axis := AXIS_X, Enable := TRUE)") && axisText.Contains("SnippetAxis_X_MC_ReadAxisStatus1(Axis := AXIS_X, Enable := TRUE)"), "MCP inserts canonical and legacy readers enabled for read-only feedback");
            var unknownAxis = await client.CallToolAsync("st_insert_instruction", new Dictionary<string, object?> { ["sessionId"] = session, ["expectedRevision"] = revision,
                ["itemId"] = item, ["offset"] = axisText.IndexOf("END_PROGRAM", StringComparison.Ordinal), ["instructionId"] = "Axis_Unknown_MC_Power" });
            WorkspaceTests.Check(unknownAxis.IsError == true, "unconfigured axis template rejected");
            var dxf = "0\nSECTION\n2\nENTITIES\n0\nLINE\n8\n0\n10\n0\n20\n0\n11\n2\n21\n0\n0\nENDSEC\n0\nEOF\n";
            await File.WriteAllTextAsync(Path.Combine(root, "line.dxf"), dxf);
            var preview = await Call(client, "dxf_preview", new() { ["sessionId"] = session, ["expectedRevision"] = revision, ["dxfPath"] = "line.dxf", ["millimetersPerUnit"] = 1 });
            var path = new MotionPathAsset { Name = "CutLine", Axes = ["X", "Y"], Segments = preview.GetProperty("contours")[0].GetProperty("Segments").Deserialize<PathSegment[]>(McpJson.Options)! };
            project = await Call(client, "path_store", new() { ["sessionId"] = session, ["expectedRevision"] = revision, ["path"] = path });
            revision = project.GetProperty("Revision").GetString()!;
            var duration1 = (await Call(client, "path_plan", new() { ["sessionId"] = session, ["pathId"] = path.Id, ["version"] = 1 })).GetProperty("DurationSeconds").GetDouble();
            preview = await Call(client, "dxf_preview", new() { ["sessionId"] = session, ["expectedRevision"] = revision, ["dxfPath"] = "line.dxf", ["millimetersPerUnit"] = 10 });
            path = path with { Version = 2, ImportScale = 10, Segments = preview.GetProperty("contours")[0].GetProperty("Segments").Deserialize<PathSegment[]>(McpJson.Options)! };
            project = await Call(client, "path_store", new() { ["sessionId"] = session, ["expectedRevision"] = revision, ["path"] = path });
            var duration10 = (await Call(client, "path_plan", new() { ["sessionId"] = session, ["pathId"] = path.Id, ["version"] = 2 })).GetProperty("DurationSeconds").GetDouble();
            WorkspaceTests.Check(duration10 > duration1, "MCP reparsed DXF scale changes actual planned time");
            Console.WriteLine($"PASS actual stdio SDK client: {tools.Count} tools, engineering workflow, revision errors, real compiler package and compiler diagnostics");
            Console.WriteLine("PASS MCP typed axis configuration, DXF preview, immutable path versions and scale-dependent planned time");
            if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(repository, "artifacts", "mcp-server", "StWorkbench.Mcp.dll")))
            {
                var launchTransport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "Shipped PowerShell launcher", Command = (Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Any(p => File.Exists(Path.Combine(p, "pwsh.exe"))) == true) ? "pwsh" : "powershell", Arguments = ["-NoProfile", "-File", Path.Combine(repository, "scripts", "start-mcp.ps1"), "-ConfigPath", configPath]
                });
                await using var launchedClient = await McpClient.CreateAsync(launchTransport);
                _ = await Call(launchedClient, "workbench_info", new());
                _ = await Call(launchedClient, "st_instructions", new() { ["search"] = "TON" });
                Console.WriteLine("PASS shipped PowerShell stdio launcher and actual authored motion function signatures");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<JsonElement> Call(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        WorkspaceTests.Check(result.IsError != true, tool + ": " + text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
    private static async Task<JsonElement> WaitBuild(McpClient client, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (true)
        {
            var result = await Call(client, "build_status", new() { ["operationId"] = id });
            if (result.GetProperty("State").GetString() is not "Queued" and not "Running") return result;
            await Task.Delay(100, deadline.Token);
        }
    }
    internal static string FindRepository()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) return directory.FullName;
        throw new Exception("Run tests from repository root.");
    }
}

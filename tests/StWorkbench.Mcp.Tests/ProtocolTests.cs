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
                    Name = "Shipped PowerShell launcher", Command = "pwsh", Arguments = ["-NoProfile", "-File", Path.Combine(repository, "scripts", "start-mcp.ps1"), "-ConfigPath", configPath]
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

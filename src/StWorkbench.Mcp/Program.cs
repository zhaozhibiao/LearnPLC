using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StWorkbench.Mcp;

if (args.Length == 1 && args[0] == "--help")
{
    Console.Error.WriteLine("ST Workbench MCP (stdio)\nUsage: StWorkbench.Mcp --config <absolute operator-config.json>\nSee docs/mcp.md. No device or motion connection occurs at startup.");
    return;
}
if (args.Length != 2 || args[0] != "--config")
{
    Console.Error.WriteLine("Expected --config <operator-config.json>. See docs/mcp.md.");
    Environment.ExitCode = 2; return;
}
try
{
    var options = ServerOptions.Load(Path.GetFullPath(args[1]));
    var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(new WorkspaceService(options.WorkspaceRoot));
    builder.Services.AddSingleton<ConfigurationService>(); builder.Services.AddSingleton<BuildService>();
    builder.Services.AddSingleton<OperationJobs>(); builder.Services.AddSingleton<DeviceService>();
    builder.Services.AddSingleton<MotionService>();
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<McpTools>(McpJson.Options);
    await builder.Build().RunAsync();
}
catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
{
    Console.Error.WriteLine("MCP startup/shutdown failed: " + error.Message);
    Environment.ExitCode = 1;
}

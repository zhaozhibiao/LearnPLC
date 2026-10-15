using System.Diagnostics;
using StWorkbench.Mcp;

internal static class ShutdownTests
{
    public static async Task RunAsync()
    {
        var jobs = new OperationJobs();
        var job = jobs.Start(async () => { await Task.Delay(Timeout.Infinite, jobs.ShutdownToken); return new { state = "Completed" }; });
        await jobs.DisposeAsync();
        WorkspaceTests.Check(jobs.Read(job.OperationId).State == "Unknown", "shutdown does not misreport interrupted device workflow completed");
        await WorkspaceTests.Reject(() => Task.FromResult(jobs.Start(() => Task.FromResult<object>(new { }))), "shutdown refuses jobs");
        var knownHosts = Path.GetTempFileName();
        try
        {
            var device = new DeviceOptions { Id = "fixture", Host = "fixture", User = "fixture", KnownHostsPath = knownHosts };
            var executable = Path.Combine(AppContext.BaseDirectory, "StWorkbench.Mcp.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            await using var transport = new MotionTransport(device, executable);
            var blocked = transport.RequestAsync("status");
            var timer = Stopwatch.StartNew();
            await WorkspaceTests.Reject(() => transport.RequestAsync("stop", new Dictionary<string, object?> { ["axis"] = "X", ["operationId"] = "stop-fixture" }), "blocked stop returns unknown after safe disconnect");
            WorkspaceTests.Check(timer.Elapsed < TimeSpan.FromSeconds(3), "stop must not wait 15 seconds behind blocked exchange");
            await WorkspaceTests.Reject(() => blocked, "inflight response becomes unknown");
            await WorkspaceTests.Reject(() => transport.RequestAsync("execute"), "closed transport never reconnects/replays");
        }
        finally { File.Delete(knownHosts); }
        Console.WriteLine("PASS MCP shutdown cancels/awaits jobs and blocked Stop closes session promptly without replay or false success");
    }
}

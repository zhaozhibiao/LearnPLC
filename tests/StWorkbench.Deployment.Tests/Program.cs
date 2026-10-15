using System.Text.Json;
using StWorkbench.Toolchain;

var mode = new DirectoryInfo(AppContext.BaseDirectory).Name;
if (mode.StartsWith("fake-ssh-", StringComparison.Ordinal))
{
    if (mode == "fake-ssh-slow") { await Task.Delay(TimeSpan.FromMinutes(5)); return 0; }
    if (!args.Contains("StrictHostKeyChecking=yes") || !args.Contains("BatchMode=yes") || args[^1] != "python3 /home/a/st-workbench-deployment/coordinator.py") return 8;
    if (mode == "fake-ssh-known-hosts" && !args.Any(argument => argument.StartsWith("UserKnownHostsFile=", StringComparison.Ordinal))) return 7;
    using var input = JsonDocument.Parse(await Console.In.ReadToEndAsync());
    if (input.RootElement.GetProperty("action").GetString() != "status") return 9;
    if (mode == "fake-ssh-invalid") { Console.WriteLine("unparseable output"); return 0; }
    var requestId = mode == "fake-ssh-wrong-id" ? "other-request" : input.RootElement.GetProperty("request_id").GetString();
    Console.WriteLine(JsonSerializer.Serialize(new { success = true, state = "stopped", request_id = requestId, active = new { build_hash = "identity", program_md5 = "md5", symbol_table_hash = "symbols" } }));
    return 0;
}
var root = Path.Combine(Path.GetTempPath(), "SSH fixture " + Guid.NewGuid().ToString("N"));
var failures = 0;
try
{
    string Fixture(string fixtureMode)
    {
        var directory = Path.Combine(root, "fake-ssh-" + fixtureMode);
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        return Path.Combine(directory, "StWorkbench.Deployment.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }
    async Task Test(string name, Func<Task> test)
    {
        try { await test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    void Check(bool value, string message) { if (!value) throw new Exception(message); }
    var target = new SshTarget("192.168.1.10", "a");
    var request = new DeploymentRequest("status", "request-1");
    await Test("SSH uses pinned host policy and JSON stdin", async () =>
    {
        var result = await new RemoteDeploymentClient(Fixture("success")).ExecuteAsync(target, request);
        Check(result.Success && result.State == "stopped", result.Message);
        Check(result.Active!.Value.GetProperty("program_md5").GetString() == "md5", "identity missing");
    });
    await Test("session known-hosts pin is passed as one argument", async () =>
    {
        var result = await new RemoteDeploymentClient(Fixture("known-hosts"), knownHostsPath: Path.Combine(root, "known hosts pin")).ExecuteAsync(target, request);
        Check(result.Success, result.Message);
    });
    foreach (var fixtureMode in new[] { "invalid", "wrong-id" }) await Test("SSH " + fixtureMode + " outcome is unknown", async () =>
    {
        var result = await new RemoteDeploymentClient(Fixture(fixtureMode)).ExecuteAsync(target, request);
        Check(!result.Success && result.State == "unknown", "unverified response accepted");
    });
    await Test("SSH injection host refused", async () =>
    {
        try { await new RemoteDeploymentClient(Fixture("success")).ExecuteAsync(new("host;touch invalid", "a"), request); throw new Exception("unsafe host accepted"); }
        catch (ArgumentException) { }
    });
    await Test("SSH timeout reports unknown without retry", async () =>
    {
        var result = await new RemoteDeploymentClient(Fixture("slow"), TimeSpan.FromMilliseconds(400)).ExecuteAsync(target, request);
        Check(!result.Success && result.State == "unknown", "timeout hidden");
    });
    await Test("SSH cancellation propagates", async () =>
    {
        using var cancellation = new CancellationTokenSource(400);
        try { await new RemoteDeploymentClient(Fixture("slow")).ExecuteAsync(target, request, cancellation.Token); throw new Exception("cancellation missing"); }
        catch (OperationCanceledException) { }
    });
    await Test("missing SSH executable returns unknown", async () =>
    {
        var result = await new RemoteDeploymentClient(Path.Combine(root, "missing-ssh.exe")).ExecuteAsync(target, request);
        Check(!result.Success && result.State == "unknown", "missing executable not handled");
    });
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
return failures == 0 ? 0 : 1;

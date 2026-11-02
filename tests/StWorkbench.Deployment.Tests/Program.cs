using System.Text.Json;
using StWorkbench.Toolchain;

var mode = new DirectoryInfo(AppContext.BaseDirectory).Name;
if (mode.StartsWith("fake-ssh-", StringComparison.Ordinal))
{
    if (mode == "fake-ssh-slow") { await Task.Delay(TimeSpan.FromMinutes(5)); return 0; }
    if (!args.Contains("StrictHostKeyChecking=yes") || !args.Contains("BatchMode=yes") || args[^1] != "/home/a/st-workbench-deployment/st-deployment-coordinator") return 8;
    if (mode == "fake-ssh-known-hosts" && !args.Any(argument => argument.StartsWith("UserKnownHostsFile=", StringComparison.Ordinal))) return 7;
    using var input = JsonDocument.Parse(await Console.In.ReadToEndAsync());
    if (mode == "fake-ssh-hardware-apply")
    {
        if (input.RootElement.GetProperty("action").GetString() != "hardware-apply") return 9;
        var permitted = new[] { "action", "request_id", "build_hash", "expected_active_build_hash", "package_base64", "package_sha256", "package_path" };
        if (input.RootElement.EnumerateObject().Any(property => !permitted.Contains(property.Name))) return 10;
    }
    else if (mode == "fake-ssh-configuration-apply")
    {
        if (input.RootElement.GetProperty("action").GetString() != "configuration-apply" || input.RootElement.GetProperty("configuration_identity").GetString() != new string('C', 64)) return 9;
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, state = "configuration-applied", request_id = input.RootElement.GetProperty("request_id").GetString(), axis_ready = true,
            configuration_plan = new { build_hash = new string('A', 64), kind = "axis", identity = new string('C', 64) } }));
        return 0;
    }
    else if (input.RootElement.GetProperty("action").GetString() != "status") return 9;
    if (mode == "fake-ssh-invalid") { Console.WriteLine("unparseable output"); return 0; }
    var requestId = mode == "fake-ssh-wrong-id" ? "other-request" : input.RootElement.GetProperty("request_id").GetString();
    Console.WriteLine(JsonSerializer.Serialize(new { success = true, state = "stopped", request_id = requestId, active = new { build_hash = "identity", program_md5 = "md5", symbol_table_hash = "symbols" } }));
    return 0;
}
var root = Path.Combine(Path.GetTempPath(), "SSH fixture " + Guid.NewGuid().ToString("N"));
var failures = 0;
try { await EngineeringClientTests.RunAsync(); } catch (Exception error) { failures++; Console.WriteLine("FAIL engineering transport: " + error.Message); }
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
    await Test("configuration apply SSH carries bound identity and parses verified plan", async () =>
    {
        var result = await new RemoteDeploymentClient(Fixture("configuration-apply")).ExecuteAsync(target, new("configuration-apply", "config1", new string('A', 64), ConfigurationIdentity: new string('C', 64)));
        Check(result.Success && result.AxisReady == true, result.Message);
        Check(ProgramConfigurationPlan.Read(result, new string('A', 64), "configuration-applied").Kind == "axis", "configuration plan lost in SSH transport");
    });
    await Test("deployment diagnostics omit package payload", () =>
    {
        var request = new DeploymentRequest("hardware-apply", "hardware-auth", PackageBase64: "fixture-private-payload");
        Check(!request.ToString().Contains("fixture-private-payload", StringComparison.Ordinal), "package payload exposed by record diagnostics");
        return Task.CompletedTask;
    });
    await Test("hardware application request has no administrator secret field", () =>
    {
        Check(typeof(DeploymentRequest).GetProperty("AdministrativePassword") is null, "request still accepts administrator credentials");
        return Task.CompletedTask;
    });
    await Test("hardware application SSH JSON contains only deployment fields", async () =>
    {
        var result = await new RemoteDeploymentClient(Fixture("hardware-apply")).ExecuteAsync(target, new("hardware-apply", "hardware-broker", new string('A', 64)));
        Check(result.Success, result.Message);
    });
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
failures += await ProgramDownloadWorkflowTests.RunAsync();
return failures == 0 ? 0 : 1;

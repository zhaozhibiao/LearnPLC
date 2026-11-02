using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using StWorkbench.Core;
using StWorkbench.Toolchain;

namespace StWorkbench.Tools;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        var projectPath = Path.Combine(root, "examples", "is620n-turn-test", "TurnTest.stproj");
        Console.WriteLine($"Loading project from: {projectPath}");
        var store = new ProjectStore();
        var loaded = await store.LoadAsync(projectPath);
        var project = loaded.Project;

        Console.WriteLine("Building hardware package...");
        var buildDir = Path.Combine(root, "artifacts", "hardware-builds");
        var builder = new HardwarePackageBuilder();
        var result = await builder.BuildAsync(project, buildDir);
        if (!result.Success)
        {
            Console.WriteLine("Build failed:");
            foreach (var err in result.Errors) Console.WriteLine("  " + err);
            return 1;
        }

        Console.WriteLine($"Package built successfully: {result.PackagePath}");
        Console.WriteLine($"BuildHash: {result.BuildHash}");
        Console.WriteLine($"HardwareHash: {result.HardwareHash}");

        using (var zip = ZipFile.OpenRead(result.PackagePath))
        {
            var machineEntry = zip.GetEntry("machine-config.json") ?? throw new Exception("Missing machine-config.json");
            using var reader = new StreamReader(machineEntry.Open());
            var machineJson = await reader.ReadToEndAsync();
            Console.WriteLine("machine-config.json contents:\n" + machineJson);
            var ecatEntry = zip.GetEntry("ethercat-runtime.json") ?? throw new Exception("Missing ethercat-runtime.json");
            using var ecatReader = new StreamReader(ecatEntry.Open());
            Console.WriteLine("ethercat-runtime.json contents:\n" + await ecatReader.ReadToEndAsync());
        }

        var bytes = await File.ReadAllBytesAsync(result.PackagePath);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var base64 = Convert.ToBase64String(bytes);

        var target = new SshTarget("192.168.1.31", "a");
        var client = new RemoteDeploymentClient();

        Console.WriteLine("Checking remote coordinator status...");
        var status = await client.ExecuteAsync(target, new DeploymentRequest("status", Guid.NewGuid().ToString("N")));
        Console.WriteLine($"Status: {status.State}, success: {status.Success}");
        string? baseline = status.Active?.GetProperty("build_hash").GetString();

        Console.WriteLine("Staging hardware package on target...");
        var staged = await client.ExecuteAsync(target, new DeploymentRequest("hardware-stage", Guid.NewGuid().ToString("N"),
            result.BuildHash, baseline, base64, sha256));
        Console.WriteLine($"Stage result: {staged.State}, success: {staged.Success}, message: {staged.Message}");

        Console.WriteLine("Validating hardware package on target...");
        var validated = await client.ExecuteAsync(target, new DeploymentRequest("hardware-validate", Guid.NewGuid().ToString("N"),
            result.BuildHash, baseline));
        Console.WriteLine($"Validate result: {validated.State}, success: {validated.Success}, message: {validated.Message}");

        if (status.State == "running")
        {
            Console.WriteLine("Stopping running PLC program...");
            var stopped = await client.ExecuteAsync(target, new DeploymentRequest("stop", Guid.NewGuid().ToString("N"), null, baseline));
            Console.WriteLine($"Stop result: {stopped.State}, success: {stopped.Success}");
        }

        Console.WriteLine("Applying hardware bundle via resident broker...");
        var applied = await client.ExecuteAsync(target, new DeploymentRequest("hardware-apply", Guid.NewGuid().ToString("N"),
            result.BuildHash, baseline));
        Console.WriteLine($"Apply result: {applied.State}, success: {applied.Success}, message: {applied.Message}");

        Console.WriteLine("Checking final status...");
        var finalStatus = await client.ExecuteAsync(target, new DeploymentRequest("status", Guid.NewGuid().ToString("N")));
        Console.WriteLine($"Final status: {finalStatus.State}, success: {finalStatus.Success}");

        return 0;
    }
}

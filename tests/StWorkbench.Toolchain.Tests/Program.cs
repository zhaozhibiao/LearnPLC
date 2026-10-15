using System.Diagnostics;
using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

// When copied to a named directory this executable is a real child-process compiler fixture.
var fixtureMode = new DirectoryInfo(AppContext.BaseDirectory).Name;
if (fixtureMode.StartsWith("fake-", StringComparison.Ordinal))
{
    if (args.Contains("--child")) { await Task.Delay(TimeSpan.FromMinutes(5)); return 0; }
    if (args.Contains("--version")) { Console.WriteLine("STruC++ 0.6.1"); return 0; }
    if (fixtureMode == "fake-slow")
    {
        var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        childStart.ArgumentList.Add("--child");
        using var child = Process.Start(childStart)!;
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "child.pid"), child.Id.ToString());
        await Task.Delay(TimeSpan.FromMinutes(5)); return 0;
    }
    var outputIndex = Array.IndexOf(args, "-o");
    if (outputIndex < 1 || args.Length != outputIndex + 2) return 9;
    var source = args[0];
    if (!File.Exists(source)) return 8;
    if (fixtureMode == "fake-fail")
    {
        Console.Error.WriteLine($"{source}:3:7: error: Undeclared variable 'x'");
        Console.Error.WriteLine("unstructured compiler detail");
        return 1;
    }
    if (fixtureMode == "fake-empty") return 0;
    if (fixtureMode == "fake-zero-error") Console.Error.WriteLine($"{source}:4:2: error: Invalid type");
    if (fixtureMode == "fake-many-diagnostics")
    {
        for (var i = 0; i < 1100; i++) Console.Error.WriteLine($"{source}:2:2: warning: warning {i}");
        Console.Error.WriteLine($"{source}:4:2: error: Invalid type after many warnings");
    }
    if (fixtureMode == "fake-flood") Console.WriteLine(new string('x', 100000));
    Console.Error.WriteLine($"{source}:2:4: warning: Narrowing conversion");
    await File.WriteAllTextAsync(args[outputIndex + 1], "// generated from " + await File.ReadAllTextAsync(source));
    if (fixtureMode != "fake-half") await File.WriteAllTextAsync(Path.ChangeExtension(args[outputIndex + 1], ".hpp"), "// declarations");
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "ST compiler tests " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = 0;
try
{
    async Task Test(string name, Func<Task> test)
    {
        try { await test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    BuildSnapshot Snapshot(string path = "sources/Main with spaces.st") => new(Guid.NewGuid(), "Test", "source-identity", "hardware-identity",
        [new(Guid.NewGuid(), path, "PROGRAM Main\nEND_PROGRAM")], []);
    string Fixture(string mode)
    {
        var directory = Path.Combine(root, "compiler path with spaces", "fake-" + mode);
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        return Path.Combine(directory, "StWorkbench.Toolchain.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }
    await Test("success preserves identity and isolated sources", async () =>
    {
        var snapshot = Snapshot();
        var result = await new ExternalCompiler().CompileAsync(snapshot, Fixture("success"), root);
        Check(result.Success, "expected successful child compilation: " + result.Log);
        Check(result.SourceHash == snapshot.SourceHash, "source identity changed");
        Check(result.CompilerVersion == "STruC++ 0.6.1", "actual version missing");
        Check(File.Exists(Path.Combine(result.OutputDirectory, "program.cpp")), "generated code missing");
        Check(File.Exists(Path.Combine(result.OutputDirectory, "program.hpp")), "generated header missing");
        Check(result.Diagnostics.Single().File == snapshot.Sources[0].RelativePath, "diagnostic not mapped to original source");
        Check(result.Diagnostics.Single().Line == 2 && result.Diagnostics.Single().Column == 4, "warning position lost");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "build-identity.json")));
        Check(manifest.RootElement.GetProperty("SourceHash").GetString() == snapshot.SourceHash, "manifest identity lost");
        Check(manifest.RootElement.GetProperty("RuntimeCompatibility").GetString() == "unverified", "artifact must not claim runtime compatibility");
        var second = await new ExternalCompiler().CompileAsync(snapshot, Fixture("success"), root);
        Check(second.OutputDirectory != result.OutputDirectory, "workspace reused");
    });
    await Test("failure retains located error and raw output", async () =>
    {
        var result = await new ExternalCompiler().CompileAsync(Snapshot(), Fixture("fail"), root);
        Check(!result.Success, "failed process accepted");
        Check(result.Diagnostics.Any(d => d.Line == 3 && d.Column == 7 && d.Severity == "error"), "located error lost");
        Check(result.Log.Contains("unstructured compiler detail"), "unparsed output lost");
    });
    foreach (var mode in new[] { "empty", "half" }) await Test("exit zero rejects " + mode + " output", async () =>
    {
        var result = await new ExternalCompiler().CompileAsync(Snapshot(), Fixture(mode), root);
        Check(!result.Success && result.Diagnostics.Any(d => d.Severity == "error"), "missing generated files accepted");
    });
    await Test("error diagnostic rejects exit zero with files", async () =>
    {
        var result = await new ExternalCompiler().CompileAsync(Snapshot(), Fixture("zero-error"), root);
        Check(!result.Success && result.Diagnostics.Any(d => d.Line == 4 && d.Severity == "error"), "reported compiler error accepted");
    });
    await Test("diagnostic capacity cannot hide error status", async () =>
    {
        var result = await new ExternalCompiler().CompileAsync(Snapshot(), Fixture("many-diagnostics"), root);
        Check(!result.Success, "diagnostic cap concealed compiler error");
        Check(result.Diagnostics.Count <= 1001, "diagnostic memory unbounded");
    });
    await Test("version mismatch refuses compilation", async () =>
    {
        var result = await new ExternalCompiler(expectedCompilerVersion: "STruC++ 9.9.9").CompileAsync(Snapshot(), Fixture("success"), root);
        Check(!result.Success && result.Log.Contains("version", StringComparison.OrdinalIgnoreCase), "compiler mismatch accepted");
        Check(result.CompilerVersion == "STruC++ 0.6.1", "mismatch obscured actual compiler");
    });
    await Test("cancel terminates compiler", async () =>
    {
        var fixture = Fixture("slow");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var watch = Stopwatch.StartNew();
        try { await new ExternalCompiler().CompileAsync(Snapshot(), fixture, root, cancellation.Token); throw new Exception("cancellation not propagated"); }
        catch (OperationCanceledException) { Check(watch.Elapsed < TimeSpan.FromSeconds(5), "cancel too slow"); }
        var childId = int.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(fixture)!, "child.pid")));
        try { using var child = Process.GetProcessById(childId); Check(child.HasExited, "descendant still alive after cancellation"); }
        catch (ArgumentException) { /* Process already reaped. */ }
    });
    await Test("timeout returns explicit failure", async () =>
    {
        var result = await new ExternalCompiler(TimeSpan.FromMilliseconds(500)).CompileAsync(Snapshot(), Fixture("slow"), root);
        Check(!result.Success && result.Log.Contains("timed out", StringComparison.OrdinalIgnoreCase), "timeout missing");
    });
    await Test("output flood is bounded", async () =>
    {
        var result = await new ExternalCompiler(maximumLogCharacters: 4096).CompileAsync(Snapshot(), Fixture("flood"), root);
        Check(result.Success, "flood prevented valid compile");
        Check(result.Log.Length <= 4096 && result.Log.Contains("truncated"), "output not bounded");
    });
    await Test("source escape is refused", async () =>
    {
        var result = await new ExternalCompiler().CompileAsync(Snapshot("../escaped.st"), Fixture("success"), root);
        Check(!result.Success && result.Diagnostics.Any(d => d.Severity == "error"), "source escape accepted");
    });
    await Test("missing executable is explicit failure", async () =>
    {
        var result = await new ExternalCompiler().CompileAsync(Snapshot(), Path.Combine(root, "missing.exe"), root);
        Check(!result.Success && result.Diagnostics.Any(d => d.Severity == "error"), "process launch failure hidden");
    });
    var realCompiler = Environment.GetEnvironmentVariable("ST_COMPILER_PATH");
    if (!string.IsNullOrWhiteSpace(realCompiler)) await Test("installed STruCpp compiler smoke", () => RealCompilerSmoke.RunAsync(realCompiler));
    var adapter = Environment.GetEnvironmentVariable("ST_COMPILER_ADAPTER");
    var distribution = Environment.GetEnvironmentVariable("ST_COMPILER_DISTRIBUTION");
    if (!string.IsNullOrWhiteSpace(adapter) && !string.IsNullOrWhiteSpace(distribution))
        await Test("real hardware topology package", () => RealHardwarePackageSmoke.RunAsync(adapter, distribution));
    if (!string.IsNullOrWhiteSpace(adapter) && !string.IsNullOrWhiteSpace(distribution))
        await Test("real basic axis library and native bundle", () => RealAxisPackageSmoke.RunAsync(adapter, distribution));
    if (!string.IsNullOrWhiteSpace(adapter) && !string.IsNullOrWhiteSpace(distribution))
        await Test("real axis cycle configuration candidate and legacy gate", () => RealMotionCyclePackageSmoke.RunAsync(adapter, distribution));
    if (!string.IsNullOrWhiteSpace(adapter) && !string.IsNullOrWhiteSpace(distribution))
        await Test("real continuous path package, native dependencies and asset identity", () => RealPathPackageSmoke.RunAsync(adapter, distribution));
    if (!string.IsNullOrWhiteSpace(adapter) && !string.IsNullOrWhiteSpace(distribution))
        await Test("real Runtime staging package", () => RealPackageSmoke.RunAsync(adapter, distribution));
}
finally { Directory.Delete(root, true); }
return failures == 0 ? 0 : 1;


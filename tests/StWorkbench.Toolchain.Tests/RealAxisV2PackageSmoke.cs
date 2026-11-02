using System.IO.Compression;
using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class RealAxisV2PackageSmoke
{
    public static async Task RunAsync(string adapter, string distribution)
    {
        var main = new ProjectItem();
        var project = new StProject { Items = [main], Tasks = [new PlcTask { ProgramId = main.Id }] };
        project.Hardware.Axes.Add(new("X", "Axis1", "mm", -100, 100) { MaximumVelocity = 5, MaximumAcceleration = 10, MaximumDeceleration = 12, MaximumJerk = 0 });
        const string legacy = "PROGRAM Main\nVAR MoveAxis : MC_MoveAbsolute; LegacyMove : MC_MoveAbsolute; Jog : MC_MoveVelocity; Stop : MC_Stop; END_VAR\nMoveAxis(Axis := AXIS_X, Execute := FALSE, Position := 1.0, Velocity := 1.0);\nLegacyMove(Axis := AXIS_X, Execute := FALSE, Position := 2.0, Velocity := 1.0);\nJog(Axis := AXIS_X, Execute := FALSE, Velocity := 1.0);\nStop(Axis := AXIS_X, Execute := FALSE);\nEND_PROGRAM";
        var names = new Dictionary<Guid, string> { [main.Id] = "Main" };
        var root = Path.GetFullPath("artifacts/axis-v2-package-verify");
        var builder = new PackageBuilder();
        async Task<PackageResult> Build(string source) => await builder.BuildAsync(ProjectValidator.Capture(project, [new(main.Id, main.SourcePath, source)]), names, "node", adapter, distribution, root);
        var v1 = await Build(legacy);
        Check(v1.Success, "real v1 compilation: " + v1.Compilation.Log);
        using (var zip = ZipFile.OpenRead(v1.PackagePath))
        {
            using var axis = JsonDocument.Parse(zip.GetEntry("axis-manifest.json")!.Open());
            Check(axis.RootElement.GetProperty("InterfaceVersion").GetString() == "workbench-axis-v1", "omitted inputs retain v1");
            Check(!axis.RootElement.TryGetProperty("CommandBytes", out _), "v1 manifest stays unchanged");
        }
        project.Hardware.Axes[0] = project.Hardware.Axes[0] with { MaximumJerk = 1000 };
        var dynamicSource = legacy.Replace("Position := 1.0, Velocity := 1.0", "Position := 1.0, Velocity := 1.0, Acceleration := 2.0, Deceleration := 3.0, Jerk := 0.0", StringComparison.Ordinal);
        var v2 = await Build(dynamicSource);
        Check(v2.Success, "real v2 named-input compilation: " + v2.Compilation.Log);
        var absoluteOnlySource = string.Join("\n", dynamicSource.Split('\n').Where(line => !line.StartsWith("Jog(", StringComparison.Ordinal) && !line.StartsWith("Stop(", StringComparison.Ordinal)));
        var absoluteOnly = await Build(absoluteOnlySource);
        Check(absoluteOnly.Success, "absolute-only v2 real compilation");
        using (var onlyMoveZip = ZipFile.OpenRead(absoluteOnly.PackagePath))
        {
            using var onlyMoveManifest = JsonDocument.Parse(onlyMoveZip.GetEntry("axis-manifest.json")!.Open());
            Check(onlyMoveManifest.RootElement.GetProperty("RequiredRuntimeCapabilities").GetArrayLength() == 1, "absolute-only profile does not require velocity capability");
        }
        Check(v2.BuildHash != v1.BuildHash, "v2 distinct build identity");
        await File.WriteAllTextAsync(Path.Combine(root, "last-v1-package.txt"), v1.PackagePath);
        await File.WriteAllTextAsync(Path.Combine(root, "last-v2-package.txt"), v2.PackagePath);
        using (var zip = ZipFile.OpenRead(v2.PackagePath))
        {
            using var axis = JsonDocument.Parse(zip.GetEntry("axis-manifest.json")!.Open());
            using var manifest = JsonDocument.Parse(zip.GetEntry("build-manifest.json")!.Open());
            Check(axis.RootElement.GetProperty("InterfaceVersion").GetString() == "workbench-axis-v2", "v2 manifest version");
            Check(axis.RootElement.GetProperty("RequiredRuntimeCapabilities").GetArrayLength() == 2, "dynamic move with omitted Jog/Stop requires velocity capability");
            Check(axis.RootElement.GetProperty("Axes")[0].GetProperty("MaximumJerk").GetDouble() == 1000, "omitted v2 Jog/Stop inherit explicit positive jerk default");
            Check(axis.RootElement.GetProperty("CommandBytes").GetInt32() == 4112 && axis.RootElement.GetProperty("FeedbackBytes").GetInt32() == 6656, "v2 image sizes");
            Check(axis.RootElement.GetProperty("RequiredRuntimeCapabilities").EnumerateArray().Any(c => c.GetString() == "workbench_axis_move_profile_v2"), "v2 axis native capability");
            Check(manifest.RootElement.GetProperty("AxisInterfaceVersion").GetString() == axis.RootElement.GetProperty("InterfaceVersion").GetString(), "top-level/axis interface parity");
            Check(manifest.RootElement.GetProperty("AxisCommandBytes").GetInt32() == axis.RootElement.GetProperty("CommandBytes").GetInt32() && manifest.RootElement.GetProperty("AxisFeedbackBytes").GetInt32() == axis.RootElement.GetProperty("FeedbackBytes").GetInt32(), "top-level/axis image-size parity");
            Check(manifest.RootElement.GetProperty("RequiredRuntimeCapabilities").EnumerateArray().Select(c => c.GetString()).SequenceEqual(axis.RootElement.GetProperty("RequiredRuntimeCapabilities").EnumerateArray().Select(c => c.GetString())), "move-only required capabilities exact parity");
            Check(manifest.RootElement.GetProperty("AxisExportMarker").GetString() == "workbench_axis_v2", "v2 marker declared");
            Check(zip.GetEntry("native/src/axis_worker.cpp") is not null && !zip.Entries.Any(e => e.FullName.EndsWith(".py", StringComparison.Ordinal)), "native v2 worker sources without Python");
            using var nativeManifest = JsonDocument.Parse(zip.GetEntry("build-manifest.json")!.Open());
            Check(nativeManifest.RootElement.GetProperty("NativeWorkerInterfaceVersion").GetString() == NativeWorkerBundle.InterfaceVersion, "native source contract marked");
            Check(nativeManifest.RootElement.GetProperty("NativeWorkerTargets")[0].GetString() == "st-axis-worker", "native axis target selected");
            var nativeSources = nativeManifest.RootElement.GetProperty("NativeSources").EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
            var canonicalNative = string.Concat(nativeSources.Select(p => p.Name + "\0" + p.Value.GetString() + "\n"));
            var expectedNative = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalNative))).ToLowerInvariant();
            Check(nativeManifest.RootElement.GetProperty("NativeSourceBundleSha256").GetString() == expectedNative, "native source bundle canonical hash");
            using var bridge = new StreamReader(zip.GetEntry("axis_bridge.cpp")!.Open());
            Check((await bridge.ReadToEndAsync()).Contains("workbench_axis_v2", StringComparison.Ordinal), "v2 marker exported");
            using var header = new StreamReader(zip.GetEntry("generated.hpp")!.Open());
            Check((await header.ReadToEndAsync()).Contains("ACCELERATION", StringComparison.Ordinal), "compiler emitted dynamic inputs");
            using var generated = new StreamReader(zip.GetEntry("generated.cpp")!.Open());
            var cpp = await generated.ReadToEndAsync();
            Check(cpp.Contains("MC_STOP::MC_STOP()\n    : DECELERATION(0.0), JERK(0.0)", StringComparison.Ordinal) && cpp.Contains("COMMAND.JERK = JERK;", StringComparison.Ordinal), "real omitted Stop inputs initialize inheritance sentinels and forward them to v2 arbiter");
        }
        foreach (var source in new[] {
            string.Join("\n", dynamicSource.Split('\n').Where(line => !line.StartsWith("Jog(", StringComparison.Ordinal))),
            legacy.Replace("Velocity := 1.0);\nStop", "Velocity := 1.0, Acceleration := 2.0, Deceleration := 3.0, Jerk := 0.0);\nStop", StringComparison.Ordinal),
            legacy.Replace("Stop(Axis := AXIS_X, Execute := FALSE)", "Stop(Axis := AXIS_X, Execute := FALSE, Deceleration := 3.0, Jerk := 0.0)", StringComparison.Ordinal),
            dynamicSource.Replace("Jog(Axis := AXIS_X, Execute := FALSE, Velocity := 1.0)", "Jog(Axis := AXIS_X, Execute := FALSE, Velocity := 1.0, Acceleration := 2.0, Deceleration := 3.0, Jerk := 0.0)", StringComparison.Ordinal).Replace("Stop(Axis := AXIS_X, Execute := FALSE)", "Stop(Axis := AXIS_X, Execute := FALSE, Deceleration := 3.0, Jerk := 0.0)", StringComparison.Ordinal)
        })
        {
            Check(AxisControlBinding.UsesDynamicVelocityProfile([new(main.Id, main.SourcePath, source)]), "phase2 source selects capability");
            var phase2 = await Build(source);
            Check(phase2.Success, "real phase2 named-input compilation: " + phase2.Compilation.Log);
            using var zip = ZipFile.OpenRead(phase2.PackagePath);
            foreach (var file in new[] { "axis-manifest.json", "build-manifest.json" })
            {
                using var manifest = JsonDocument.Parse(zip.GetEntry(file)!.Open());
                var caps = manifest.RootElement.GetProperty("RequiredRuntimeCapabilities").EnumerateArray().Select(c => c.GetString()).ToArray();
                Check(caps.SequenceEqual(new[] { "workbench_axis_move_profile_v2", "workbench_axis_velocity_profile_v2" }), "phase2 exact required capability union");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "last-phase2-package.txt"), phase2.PackagePath);
        }
        project.Hardware.Axes[0] = project.Hardware.Axes[0] with { MaximumJerk = null };
        try { await Build(dynamicSource); throw new Exception("missing inherited jerk accepted"); }
        catch (InvalidDataException ex) when (ex.Message.Contains("explicit", StringComparison.Ordinal)) { }
        Console.WriteLine("PASS real v1 omitted inputs, v2 named inputs, manifest/capabilities/export/worker/hash and explicit defaults");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}


using System.IO.Compression;
using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class RealAxisPackageSmoke
{
    public static async Task RunAsync(string adapter, string distribution)
    {
        var main = new ProjectItem();
        var project = new StProject { Name = "Basic axis control", Items = [main], Tasks = [new PlcTask { ProgramId = main.Id }] };
        project.Hardware.Axes.Add(new("X", "Axis1", "mm", -100, 100));
        var source = "PROGRAM Main\nVAR\n EnableAxis : BOOL;\n ExecuteReset : BOOL;\n ExecuteHome : BOOL;\n ExecuteMove : BOOL;\n ExecuteJog : BOOL;\n ExecuteStop : BOOL;\n Power : MC_Power;\n Reset : MC_Reset;\n Home : MC_Home;\n MoveAxis : MC_MoveAbsolute;\n Jog : MC_MoveVelocity;\n Stop : MC_Stop;\n Status : MC_ReadAxisStatus;\n Position : MC_ReadActualPosition;\n Velocity : MC_ReadActualVelocity;\nEND_VAR\nPower(Axis := AXIS_X, Enable := EnableAxis);\nReset(Axis := AXIS_X, Execute := ExecuteReset);\nHome(Axis := AXIS_X, Execute := ExecuteHome AND Power.Status);\nMoveAxis(Axis := AXIS_X, Execute := ExecuteMove AND Power.Status, Position := 1.0, Velocity := 1.0);\nJog(Axis := AXIS_X, Execute := ExecuteJog AND Power.Status, Velocity := 1.0);\nStop(Axis := AXIS_X, Execute := ExecuteStop);\nStatus(Axis := AXIS_X, Enable := TRUE);\nPosition(Axis := AXIS_X, Enable := TRUE);\nVelocity(Axis := AXIS_X, Enable := TRUE);\nEND_PROGRAM\n";
        SourceDocument[] docs = [new(main.Id, main.SourcePath, source)];
        var snapshot = ProjectValidator.Capture(project, docs);
        var builder = new PackageBuilder();
        var root = Path.GetFullPath("artifacts/axis-package-verify");
        var names = new Dictionary<Guid, string> { [main.Id] = "Main" };
        var result = await builder.BuildAsync(snapshot, names, "node", adapter, distribution, root);
        if (!result.Success) throw new Exception("Axis candidate compiler: " + result.Compilation.Log);
        using (var zip = ZipFile.OpenRead(result.PackagePath))
        {
            using var manifest = JsonDocument.Parse(zip.GetEntry("build-manifest.json")!.Open());
            if (!manifest.RootElement.TryGetProperty("AxisInterfaceVersion", out var version) || version.GetString() != "workbench-axis-v1") throw new Exception("Basic axis interface not marked");
            foreach (var file in new[] { "axis_control.st", "axis-constants.st", "axis_bridge.hpp", "axis_bridge.cpp", "generated_axis_scan_shim.cpp", "axis_lifecycle.inc", "axis-manifest.json", "axes/axis_worker.py" })
                if (zip.GetEntry(file) is null || !manifest.RootElement.GetProperty("Files").TryGetProperty(file, out _)) throw new Exception("Axis dependency missing: " + file);
            using var axis = JsonDocument.Parse(zip.GetEntry("axis-manifest.json")!.Open());
            if (axis.RootElement.GetProperty("Axes")[0].GetProperty("ControllerAxis").GetString() != "Axis1") throw new Exception("Axis mapping differs");
            var legacyAxis = axis.RootElement.GetProperty("Axes")[0];
            const string expectedLegacyManifest = "{\"SchemaVersion\":1,\"InterfaceVersion\":\"workbench-axis-v1\",\"PlcIntervalMilliseconds\":10,\"Axes\":[{\"Index\":1,\"Name\":\"X\",\"ControllerAxis\":\"Axis1\",\"Unit\":\"mm\",\"MinPosition\":-100,\"MaxPosition\":100,\"MaximumVelocity\":null,\"MaximumAcceleration\":null,\"MaximumDeceleration\":null,\"MaximumJerk\":null}]}";
            if (axis.RootElement.GetRawText() != expectedLegacyManifest) throw new Exception("Legacy axis manifest bytes changed");
            if (legacyAxis.TryGetProperty("Mechanics", out _)) throw new Exception("Legacy axis manifest gained Mechanics");
            foreach (var cap in new[] { "MaximumVelocity", "MaximumAcceleration", "MaximumDeceleration", "MaximumJerk" })
                if (legacyAxis.GetProperty(cap).ValueKind != JsonValueKind.Null) throw new Exception("Legacy nullable dynamic cap changed: " + cap);
            using var headerReader = new StreamReader(zip.GetEntry("generated.hpp")!.Open());
            var header = await headerReader.ReadToEndAsync();
            foreach (var block in AxisControlBinding.BlockNames)
                if (System.Text.RegularExpressions.Regex.Matches(header, @"\bclass " + block.ToUpperInvariant() + @"\s*\{").Count != 1)
                    throw new Exception("Axis type must have exactly one implementation: " + block);
        }
        var failed = await builder.BuildAsync(snapshot with { Sources = [docs[0] with { Text = source.Replace("AXIS_X", "AXIS_Missing", StringComparison.Ordinal) }] }, names, "node", adapter, distribution, root);
        if (failed.Success) throw new Exception("Unknown axis packaged");
        await new ProjectStore().SaveAsync(Path.GetFullPath("examples/axis-control/AxisControl.stproj"), project, docs);
        await File.WriteAllTextAsync(Path.Combine(root, "last-package.txt"), result.PackagePath);
        Console.WriteLine("REAL axis candidate: " + result.PackagePath);
        var mechanics = new AxisMechanics
        {
            Kind = AxisKind.Rotary, FeedbackUnit = AxisFeedbackUnit.EncoderCounts,
            FeedbackCountsPerMotorRevolution = 8192, MotorRevolutions = 12, LoadRevolutions = 3,
            TravelPerLoadRevolution = 7, ReverseDirection = true, RotaryMode = RotaryMode.Modulo,
            ModuloStart = 0, ModuloPeriod = 360, HomeMode = AxisHomeMode.SwitchAndIndex,
            HomeDirection = AxisHomeDirection.Positive, HomeOffset = 3, HomeSearchVelocity = 4,
            HomeLatchVelocity = .5
        };
        var legacy = project.Hardware.Axes[0];
        project.Hardware.Axes[0] = legacy with { Unit = "deg", MinPosition = 0, MaxPosition = 360, Mechanics = mechanics };
        var configuredSnapshot = ProjectValidator.Capture(project, docs);
        project.Hardware.Axes[0] = legacy;
        var configured = await builder.BuildAsync(configuredSnapshot, names, "node", adapter, distribution, root);
        if (!configured.Success) throw new Exception("Offline mechanical axis candidate: " + configured.Compilation.Log);
        using (var zip = ZipFile.OpenRead(configured.PackagePath))
        {
            using var axis = JsonDocument.Parse(zip.GetEntry("axis-manifest.json")!.Open());
            var frozenMechanics = axis.RootElement.GetProperty("Axes")[0].GetProperty("Mechanics");
            if (frozenMechanics.GetRawText() != JsonSerializer.Serialize(mechanics)) throw new Exception("Axis manifest lost frozen mechanical/homing properties");
            if (frozenMechanics.GetProperty("Kind").GetString() != "Rotary" || frozenMechanics.GetProperty("FeedbackUnit").GetString() != "EncoderCounts")
                throw new Exception("Axis mechanical enum strings lost");
            using var hardware = JsonDocument.Parse(zip.GetEntry("hardware-configuration.json")!.Open());
            if (hardware.RootElement.GetProperty("Hardware").GetProperty("Axes")[0].GetProperty("Mechanics").GetRawText() != JsonSerializer.Serialize(mechanics))
                throw new Exception("Hardware manifest lost frozen mechanical/homing properties");
        }
        if (configured.BuildHash == result.BuildHash) throw new Exception("Mechanical configuration did not affect candidate identity");
        await File.WriteAllTextAsync(Path.Combine(root, "last-mechanical-package.txt"), configured.PackagePath);
        var path = new MotionPathAsset { Name = "Outline", Axes = ["X", "Y"], Segments = [new("Line", new(0, 0), new(1, 0))] }.Seal();
        project.Hardware.Axes.Add(new("Y", "Axis2", "mm", -100, 100));
        project.Paths.Add(path);
        var combinedSource = docs[0] with { Text = source.Replace(" EnableAxis : BOOL;", " RunPath : MC_ExecuteContinuousPath;\n EnableAxis : BOOL;", StringComparison.Ordinal).Replace("END_PROGRAM", $"RunPath(Execute := FALSE, Stop := FALSE, PathId := ULINT#{path.NumericId}, Version := ULINT#1, ContentHash := '{path.ContentHash}');\nEND_PROGRAM", StringComparison.Ordinal) };
        var combined = await builder.BuildAsync(ProjectValidator.Capture(project, [combinedSource]), names, "node", adapter, distribution, root);
        if (!combined.Success) throw new Exception("Unified axis/path candidate: " + combined.Compilation.Log);
        using (var zip = ZipFile.OpenRead(combined.PackagePath))
        {
            using var manifest = JsonDocument.Parse(zip.GetEntry("build-manifest.json")!.Open());
            if (!manifest.RootElement.TryGetProperty("AxisInterfaceVersion", out _) || !manifest.RootElement.TryGetProperty("MotionInterfaceVersion", out _)) throw new Exception("Unified axis/path dependencies missing");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "last-combined-package.txt"), combined.PackagePath);
        Console.WriteLine("REAL unified axis/path candidate: " + combined.PackagePath);
    }
}

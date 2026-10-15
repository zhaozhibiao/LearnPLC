using System.IO.Compression;
using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class RealMotionCyclePackageSmoke
{
    public static async Task RunAsync(string adapter, string distribution)
    {
        var main = new ProjectItem();
        var project = new StProject { Name = "Motion cycle candidate", Items = [main], Tasks = [new PlcTask { ProgramId = main.Id }] };
        project.Hardware.Axes.Add(new("X", "Axis1", "mm", -100, 100));
        SourceDocument[] docs = [new(main.Id, main.SourcePath, "PROGRAM Main\nVAR\n Counter : DINT;\nEND_VAR\nCounter := Counter + 1;\nEND_PROGRAM\n")];
        var names = new Dictionary<Guid, string> { [main.Id] = "Main" };
        var builder = new PackageBuilder();
        var root = Path.GetFullPath("artifacts/real-motion-cycle-package");
        var ordinary = await builder.BuildAsync(ProjectValidator.Capture(project, docs), names, "node", adapter, distribution, root);
        if (!ordinary.Success) throw new Exception(ordinary.Compilation.Log);
        using (var zip = ZipFile.OpenRead(ordinary.PackagePath))
        {
            using var manifest = JsonDocument.Parse(zip.GetEntry("build-manifest.json")!.Open());
            if (zip.GetEntry("motion-cycle.json") is not null || manifest.RootElement.TryGetProperty("MotionCycleInterfaceVersion", out _)) throw new Exception("Legacy candidate unexpectedly changed");
        }
        project.MotionCycle = new() { Enabled = true, AxisNames = ["X"], PeriodMicroseconds = 2000 };
        var snapshot = ProjectValidator.Capture(project, docs);
        var enabled = await builder.BuildAsync(snapshot, names, "node", adapter, distribution, root);
        if (!enabled.Success) throw new Exception(enabled.Compilation.Log);
        using (var zip = ZipFile.OpenRead(enabled.PackagePath))
        {
            using var manifest = JsonDocument.Parse(zip.GetEntry("build-manifest.json")!.Open());
            if (!manifest.RootElement.TryGetProperty("MotionCycleInterfaceVersion", out var version) || version.GetString() != "workbench-motion-cycle-v1") throw new Exception("Motion cycle version gate absent");
            var entry = zip.GetEntry("motion-cycle.json") ?? throw new Exception("Cycle executor configuration missing");
            using var config = JsonDocument.Parse(entry.Open());
            if (config.RootElement.GetProperty("Configuration").GetProperty("PeriodMicroseconds").GetInt32() != 2000 || config.RootElement.GetProperty("Executor").GetString() != "EcMotion") throw new Exception("Cycle configuration differs from frozen project");
            if (!manifest.RootElement.GetProperty("Files").TryGetProperty("motion-cycle.json", out _)) throw new Exception("Cycle config not covered by inventory");
        }
        var invalid = await builder.BuildAsync(snapshot with { MotionCycle = snapshot.MotionCycle! with { Priority = 0 } }, names, "node", adapter, distribution, root);
        if (invalid.Success) throw new Exception("Invalid cycle snapshot packaged");
        await File.WriteAllTextAsync(Path.Combine(root, "last-package.txt"), enabled.PackagePath);
        Console.WriteLine("REAL motion cycle candidate: " + enabled.PackagePath);
    }
}

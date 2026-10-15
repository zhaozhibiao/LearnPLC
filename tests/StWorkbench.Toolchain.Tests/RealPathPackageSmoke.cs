using System.IO.Compression;
using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class RealPathPackageSmoke
{
    public static async Task RunAsync(string adapter, string distribution)
    {
        var program = new ProjectItem();
        var path = new MotionPathAsset { Id = Guid.Parse("55555555-2222-3333-4444-555555555555"), Name = "Outline", Axes = ["X", "Y"], Segments = [new("Line", new(0, 0), new(1, 0))] }.Seal();
        var project = new StProject { Name = "Continuous virtual example", Items = [program], Tasks = [new PlcTask { ProgramId = program.Id }], Paths = [path] };
        project.Hardware.Axes.AddRange([new("X", "Axis1", "mm", -100, 100), new("Y", "Axis2", "mm", -100, 100)]);
        var text = $"PROGRAM Main\nVAR\n    RunPath : MC_ExecuteContinuousPath;\n    Trigger : BOOL := FALSE;\n    Counter : DINT;\nEND_VAR\nCounter := Counter + 1;\nTrigger := Counter >= 3;\nRunPath(Execute := Trigger, Stop := FALSE, PathId := ULINT#{path.NumericId}, Version := ULINT#1, ContentHash := '{path.ContentHash}');\nEND_PROGRAM\n";
        var docs = new[] { new SourceDocument(program.Id, program.SourcePath, text) };
        var snapshot = ProjectValidator.Capture(project, docs); var builder = new PackageBuilder();
        var root = Path.GetFullPath("artifacts/real-path-package");
        var result = await builder.BuildAsync(snapshot, new Dictionary<Guid, string> { [program.Id] = "Main" }, "node", adapter, distribution, root);
        if (!result.Success) throw new Exception("Continuous path package compilation failed: " + result.Compilation.Log);
        using (var zip = ZipFile.OpenRead(result.PackagePath))
        {
            if (zip.GetEntry("paths/motion-manifest.json") is null || zip.GetEntry("motion_bridge.cpp") is null || zip.GetEntry("runtime_lifecycle.inc") is null) throw new Exception("Motion dependency files absent");
            using var source = zip.GetEntry("build-manifest.json")!.Open(); using var manifest = JsonDocument.Parse(source);
            if (manifest.RootElement.GetProperty("MotionInterfaceVersion").GetString() != "workbench-continuous-v1") throw new Exception("Motion interface identity absent");
        }
        var failed = await builder.BuildAsync(snapshot with { Paths = [path with { ContentHash = new string('0', 64) }] }, new Dictionary<Guid, string> { [program.Id] = "Main" }, "node", adapter, distribution, root);
        if (failed.Success) throw new Exception("Tampered path resource packaged");
        await new ProjectStore().SaveAsync(Path.GetFullPath("examples/continuous-path/Continuous.stproj"), project, docs);
        await File.WriteAllTextAsync(Path.Combine(root, "last-package.txt"), result.PackagePath);
        Console.WriteLine("REAL continuous path candidate ZIP: " + result.PackagePath);
    }
}

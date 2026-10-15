using StWorkbench.Core;

public static class PathAssetTests
{
    public static async Task Run()
    {
        var main = new ProjectItem(); var project = new StProject { Items = [main], Tasks = [new PlcTask { ProgramId = main.Id }] };
        var docs = new[] { new SourceDocument(main.Id, main.SourcePath, "PROGRAM Main\nEND_PROGRAM\n") };
        project.Hardware.Axes.AddRange([new("X", "Axis1", "mm", -100, 100), new("Y", "Axis2", "mm", -100, 100)]);
        var baseline = ProjectValidator.Capture(project, docs);
        var path = new MotionPathAsset { Name = "Outline", Axes = ["X", "Y"], Segments = [new("Line", new(0, 0), new(10, 0))] }.Seal();
        project.Paths.Add(path);
        var bound = new SourceDocument(main.Id, main.SourcePath, $"PROGRAM Main\nVAR P : MC_ExecuteContinuousPath; END_VAR\nP(Execute := TRUE, PathId := ULINT#{path.NumericId}, Version := ULINT#1, ContentHash := '{path.ContentHash}');\nEND_PROGRAM\n");
        if (PathBindingValidator.Validate([bound], [path]).Count != 0) throw new Exception("FAIL: valid versioned path binding rejected");
        if (PathBindingValidator.Validate([bound with { Text = bound.Text.Replace("Version := ULINT#1", "Version := ULINT#2") }], [path]).Count == 0) throw new Exception("FAIL: unknown path version accepted");
        if (PathBindingValidator.Validate([bound with { Text = bound.Text.Replace("PathId := ULINT#" + path.NumericId, "PathId := Variable") }], [path]).Count == 0) throw new Exception("FAIL: unchecked dynamic path binding accepted");
        var global = bound with { Text = bound.Text.Replace("PROGRAM Main\nVAR P : MC_ExecuteContinuousPath; END_VAR", "VAR_GLOBAL P : MC_ExecuteContinuousPath; END_VAR\nPROGRAM Main") };
        if (PathBindingValidator.Validate([global], [path]).Count != 0 || PathBindingValidator.Validate([global with { Text = global.Text.Replace("Version := ULINT#1", "Version := ULINT#999") }], [path]).Count == 0) throw new Exception("FAIL: global motion instance bypasses path binding check");
        var array = bound with { Text = bound.Text.Replace("P : MC_ExecuteContinuousPath", "P : ARRAY [0..1] OF MC_ExecuteContinuousPath").Replace("P(Execute", "P[0](Execute") };
        if (PathBindingValidator.Validate([array], [path]).Count == 0) throw new Exception("FAIL: motion arrays bypass binding policy");
        var wrappedArray = new SourceDocument(main.Id, main.SourcePath, "FUNCTION_BLOCK Wrapper\nVAR Run : MC_ExecuteContinuousPath; END_VAR\nEND_FUNCTION_BLOCK\nPROGRAM Main\nVAR P : ARRAY [0..1] OF Wrapper; END_VAR\nP[0].Run(Execute := TRUE, PathId := ULINT#1, Version := ULINT#999, ContentHash := '" + path.ContentHash + "');\nEND_PROGRAM\n");
        if (PathBindingValidator.Validate([wrappedArray], [path]).Count == 0) throw new Exception("FAIL: nested motion arrays bypass binding policy");
        var snapshot = ProjectValidator.Capture(project, docs);
        if (snapshot.SourceHash == baseline.SourceHash || snapshot.Paths.Count != 1) throw new Exception("FAIL: path dependency absent from immutable build identity");
        path.Axes[0] = "Other";
        if (snapshot.Paths[0].Axes[0] != "X") throw new Exception("FAIL: path snapshot shares mutable arrays");
        project.Paths[0] = path with { Axes = ["X", "Y"] }; // Restore sealed content.
        var temp = Path.Combine(Path.GetTempPath(), "st-path-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            var file = Path.Combine(temp, "Path.stproj"); var store = new ProjectStore(); await store.SaveAsync(file, project, docs);
            var restored = await store.LoadAsync(file);
            if (restored.Project.Paths.Single().ContentHash != path.ContentHash || restored.Project.Paths[0].Segments[0].End.X != 10) throw new Exception("FAIL: path asset did not survive project roundtrip");
            project.Paths[0] = project.Paths[0] with { Profile = new PathProfile(Velocity: 6) };
            if (!ProjectValidator.Validate(project, docs).Any(d => d.Message.Contains("摘要"))) throw new Exception("FAIL: changed path accepted with old hash");
        }
        finally { Directory.Delete(temp, true); }
        Console.WriteLine("PASS path asset persistence, immutable snapshots, content identity and tamper rejection");
    }
}

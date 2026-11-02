using System.Text.Json;
using StWorkbench.Core;

public static class MotionCycleTests
{
    public static async Task Run()
    {
        var main = new ProjectItem();
        var project = new StProject { Items = [main], Tasks = [new PlcTask { ProgramId = main.Id }] };
        SourceDocument[] sources = [new(main.Id, main.SourcePath, "PROGRAM Main\nEND_PROGRAM\n")];
        var legacy = ProjectValidator.Capture(project, sources);
        Check(!JsonSerializer.Serialize(project).Contains("MotionCycle"), "legacy manifest has no new field");
        project.MotionCycle = new();
        Check(ProjectValidator.Capture(project, sources).HardwareHash == legacy.HardwareHash, "disabled config leaves existing build identity unchanged");
        project.Hardware.Axes.Add(new("X", "Axis1", "mm", -100, 100));
        project.MotionCycle = new() { Enabled = true, AxisNames = ["X"] };
        var snapshot = ProjectValidator.Capture(project, sources);
        Check(snapshot.MotionCycle is { Enabled: true }, "enabled cycle captured");
        Check(snapshot.SourceHash == legacy.SourceHash, "cycle config is not source code");
        var changed = ProjectValidator.Capture(withCycle(project.MotionCycle with { PeriodMicroseconds = 2000 }), sources);
        Check(changed.HardwareHash != snapshot.HardwareHash, "cycle changes hardware/build identity");
        project.MotionCycle = snapshot.MotionCycle! with { AxisNames = ["X"] };
        project.MotionCycle.AxisNames[0] = "Wrong";
        Check(snapshot.MotionCycle!.AxisNames[0] == "X", "snapshot axes frozen");
        var snapshotMutation = snapshot.MotionCycle.AxisNames; snapshotMutation[0] = "Wrong";
        Check(snapshot.MotionCycle.AxisNames[0] == "X", "snapshot does not expose mutable axis group");
        var valid = snapshot.MotionCycle!;
        Check(valid.Priority == 80, "motion default above all PLC realtime tasks");
        Check(MotionCycleValidator.Validate(valid with { Priority = 40 }, project.Hardware.Axes, []).Any(e => e.Contains("60")), "legacy low motion priority requires explicit correction");
        foreach (var bad in new[] { valid with { PeriodMicroseconds = 0 }, valid with { Priority = 90 }, valid with { Cpu = -1 }, valid with { WatchdogCycles = 0 }, valid with { SchedulingPolicy = "OTHER" }, valid with { AxisNames = [] }, valid with { AxisNames = ["X", "x"] }, valid with { AxisNames = ["Wrong"] }, valid with { AxisNames = ["x"] } })
            Check(MotionCycleValidator.Validate(bad, project.Hardware.Axes, []).Count > 0, "invalid config rejected");
        var mismatchedPath = new MotionPathAsset { Axes = ["X"], Profile = new() { CycleSeconds = .002 } };
        Check(MotionCycleValidator.Validate(valid, project.Hardware.Axes, [mismatchedPath]).Count > 0, "path sample period mismatch");
        var unmappedPath = mismatchedPath with { Axes = ["Y"], Profile = new() };
        Check(MotionCycleValidator.Validate(valid, project.Hardware.Axes, [unmappedPath]).Count > 0, "path outside configured axis group");
        project.MotionCycle = valid;
        var file = Path.GetFullPath("artifacts/motion-cycle-tests/Cycle.stproj");
        await new ProjectStore().SaveAsync(file, project, sources);
        var loaded = await new ProjectStore().LoadAsync(file);
        Check(loaded.Project.MotionCycle?.PeriodMicroseconds == 1000 && loaded.Project.MotionCycle.AxisNames.SequenceEqual(["X"]), "cycle persistence");
        Console.WriteLine("PASS motion cycle persistence, legacy identity, frozen snapshot, ranges, axes and sample period");
        StProject withCycle(MotionCycleConfiguration? config) { project.MotionCycle = config; return project; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
}

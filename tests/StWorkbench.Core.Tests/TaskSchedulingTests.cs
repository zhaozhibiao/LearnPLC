using System.Text.Json;
using StWorkbench.Core;

public static class TaskSchedulingTests
{
    public static void Run()
    {
        var main = new ProjectItem();
        var worker = new ProjectItem { Name = "Worker", SourcePath = "sources/Worker.st" };
        var project = new StProject { Items = [main, worker], Tasks = [new() { ProgramId = main.Id }, new() { Name = "Fast", ProgramId = worker.Id }] };
        SourceDocument[] sources = [new(main.Id, main.SourcePath, "PROGRAM Main\nEND_PROGRAM"), new(worker.Id, worker.SourcePath, "PROGRAM Worker\nEND_PROGRAM")];
        Check(ProjectValidator.Validate(project, sources).Count == 0, "independent enabled periodic tasks accepted");
        var task = project.Tasks[1];
        task.ProgramId = main.Id;
        Check(ProjectValidator.Validate(project, sources).Any(d => d.Message.Contains("同一个程序")), "same program cannot execute concurrently");
        task.ProgramId = worker.Id;
        var scheduling = typeof(PlcTask).GetProperty("Scheduling")!;
        Check(scheduling is not null && scheduling.GetValue(task)!.ToString() == "Ordinary", "legacy tasks default ordinary");
        scheduling!.SetValue(task, Enum.Parse(scheduling.PropertyType, "Realtime"));
        typeof(PlcTask).GetProperty("Priority")!.SetValue(task, 3);
        typeof(PlcTask).GetProperty("Cpu")!.SetValue(task, 2);
        var snapshot = ProjectValidator.Capture(project, sources);
        Check(JsonSerializer.Serialize(snapshot.Tasks[1]).Contains("\"Priority\":3"), "snapshot preserves scheduling fields");
        var hash = snapshot.SourceHash;
        typeof(PlcTask).GetProperty("Priority")!.SetValue(task, 4);
        Check(ProjectValidator.Capture(project, sources).SourceHash != hash, "scheduling changes build identity");
        Check(JsonSerializer.Serialize(snapshot.Tasks[1]).Contains("\"Priority\":3"), "snapshot detached from task edits");
        foreach (var invalid in new[] { -1, 32 })
        {
            typeof(PlcTask).GetProperty("Priority")!.SetValue(task, invalid);
            Check(ProjectValidator.Validate(project, sources).Any(d => d.Message.Contains("优先级")), "invalid IEC priority rejected");
        }
        typeof(PlcTask).GetProperty("Priority")!.SetValue(task, 10);
        project.Hardware.Axes.Add(new("X", "Axis1", "mm", -10, 10));
        project.MotionCycle = new() { Enabled = true, Cpu = 2, AxisNames = ["X"] };
        Check(ProjectValidator.Validate(project, sources).Any(d => d.Message.Contains("CPU") && d.Message.Contains("任务")), "PLC realtime must not share motion CPU");
        typeof(PlcTask).GetProperty("Cpu")!.SetValue(task, 3);
        Check(ProjectValidator.Validate(project, sources).Count == 0, "separate realtime CPU accepted");
        var restored = JsonSerializer.Deserialize<StProject>(JsonSerializer.Serialize(project))!;
        Check(scheduling.GetValue(restored.Tasks[1])!.ToString() == "Realtime", "scheduling project persistence");
        Console.WriteLine("PASS task scheduling defaults, multi-task binding, ranges, CPU isolation, persistence and frozen identity");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
}


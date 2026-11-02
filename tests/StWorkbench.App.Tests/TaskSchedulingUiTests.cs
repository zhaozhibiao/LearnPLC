using System.Reflection;
using StWorkbench.App;
using StWorkbench.Core;

public static class TaskSchedulingUiTests
{
    public static void Run()
    {
        var window = new MainWindow();
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var project = (StProject)typeof(MainWindow).GetField("project", flags)!.GetValue(window)!;
            var worker = new ProjectItem { Name = "Worker", SourcePath = "sources/Worker.st" };
            project.Items.Add(worker);
            ((Dictionary<Guid, SourceDocument>)typeof(MainWindow).GetField("sources", flags)!.GetValue(window)!).Add(worker.Id, new(worker.Id, worker.SourcePath, "PROGRAM Worker\nEND_PROGRAM"));
            var task = new PlcTask { Name = "WorkerTask", ProgramId = worker.Id, Enabled = false };
            project.Tasks.Add(task);
            var apply = typeof(MainWindow).GetMethod("ApplyTaskConfiguration", flags) ?? throw new Exception("FAIL: task scheduling editor apply missing");
            apply.Invoke(window, [task, new PlcTask { Name = task.Name, ProgramId = worker.Id, Enabled = true, Scheduling = PlcTaskScheduling.Realtime, Priority = 5, Cpu = 2 }]);
            if (!task.Enabled || task.Scheduling != PlcTaskScheduling.Realtime || task.Priority != 5 || task.Cpu != 2) throw new Exception("FAIL: UI lost task scheduling properties");
            typeof(MainWindow).GetMethod("RefreshTree", flags)!.Invoke(window, null);
            try
            {
                apply.Invoke(window, [task, new PlcTask { Name = "Invalid", ProgramId = project.Tasks[0].ProgramId, Enabled = true }]);
                throw new Exception("FAIL: UI allowed concurrent program binding");
            }
            catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
            if (task.Name != "WorkerTask" || task.Priority != 5) throw new Exception("FAIL: failed UI edit changed task");
            Console.WriteLine("PASS task UI multi-task editing, scheduling properties and invalid edit rollback");
        }
        finally { typeof(MainWindow).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false); window.Close(); }
    }
}

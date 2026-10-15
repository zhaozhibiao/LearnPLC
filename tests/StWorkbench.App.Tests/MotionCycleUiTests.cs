using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.Toolchain;

public static class MotionCycleUiTests
{
    public static void Run()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var project = (StProject)Field("project").GetValue(window)!;
            project.Hardware.Axes.Add(new("X", "Axis1", "mm", -100, 100));
            Method("RefreshHardware").Invoke(window, null);
            var enabled = window.FindControl<CheckBox>("MotionCycleEnabled") ?? throw new Exception("FAIL: independent motion cycle configuration absent");
            if (enabled.IsChecked == true) throw new Exception("FAIL: motion cycle must default off");
            enabled.IsChecked = true;
            window.FindControl<TextBox>("MotionCyclePeriod")!.Text = "2000";
            window.FindControl<ListBox>("MotionCycleAxes")!.SelectedItems!.Add("X");
            window.FindControl<Button>("ApplyMotionCycle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            if (project.MotionCycle is not { Enabled: true, PeriodMicroseconds: 2000 }) throw new Exception("FAIL: applied cycle config absent");
            if (!window.FindControl<TextBlock>("MotionCycleStatus")!.Text!.Contains("未生效")) throw new Exception("FAIL: settings falsely displayed as live");
            var previous = project.MotionCycle;
            try { Method("ApplyMotionCycleConfiguration").Invoke(window, [previous with { Priority = 0 }]); throw new Exception("FAIL: invalid UI configuration accepted"); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
            if (project.MotionCycle != previous) throw new Exception("FAIL: invalid apply changed valid config");
            window.FindControl<TabControl>("WorkbenchTabs")!.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            ((ListBox)Field("axisList").GetValue(window)!).SelectedIndex = 0;
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "删除选中轴").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            if (project.Hardware.Axes.Count != 1) throw new Exception("FAIL: deleting cycle-referenced axis invalidated project");
            if (Environment.GetEnvironmentVariable("ST_CYCLE_COMPILE_VERIFY") == "1")
            {
                Pump(window.GeneratePackageAsync());
                if (!window.FindControl<Button>("DownloadButton")!.IsEnabled) throw new Exception("FAIL: motion-cycle configured UI candidate compile");
                Pump(window.SaveAtAsync(Path.GetFullPath("examples/motion-cycle/Cycle.stproj")));
                Console.WriteLine("PASS real UI motion cycle candidate compilation and persisted example");
            }
            else
            {
                Field("candidatePackage").SetValue(window, new PackageResult(new(true, [], "", "", "", ""), "fixture", new string('A', 64), ""));
                var snapshot = ProjectValidator.Capture(project, [(SourceDocument)((Dictionary<Guid, SourceDocument>)Field("sources").GetValue(window)!).Values.First()]);
                Field("candidateSourceHash").SetValue(window, snapshot.SourceHash); Field("candidateHardwareHash").SetValue(window, snapshot.HardwareHash);
            }
            Method("ApplyCompiledCandidate").Invoke(window, [new DeploymentResponse(true, "compiled", "fixture", "", null, null, MotionCycleReady: false, MotionCycleMessage: "physical adapter absent")]);
            if (!window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("不可激活")) throw new Exception("FAIL: unavailable motion cycle backend did not gate activation");
            using var report = JsonDocument.Parse("{\"KernelRelease\":\"fixture-rt\",\"RealtimeKernel\":true,\"CpuCount\":8,\"PlcThreads\":[{\"Name\":\"TASK_CYCLIC\",\"Tid\":1,\"Policy\":\"SCHED_OTHER\",\"Priority\":0,\"Cpus\":[0,1]}],\"MotionService\":{\"Name\":\"ecmotion-rt.service\",\"State\":\"inactive\",\"Threads\":[]},\"MetricsAvailable\":false}");
            Method("ApplyMotionRealtimeReport").Invoke(window, [report.RootElement]);
            var status = window.FindControl<TextBox>("MotionRealtimeStatus")!.Text!;
            if (!status.Contains("SCHED_OTHER") || !status.Contains("不可用") || !status.Contains("fixture-rt")) throw new Exception("FAIL: actual scheduler and missing stats must be distinguished");
            window.FindControl<TabControl>("WorkbenchTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            var taskPane = ((IEnumerable<TabItem>)window.FindControl<TabControl>("WorkbenchTabs")!.ItemsSource!).ElementAt(1).Content as ScrollViewer;
            taskPane!.Offset = new Vector(0, 380); Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/st-workbench-motion-cycle.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            var file = Path.GetFullPath("artifacts/motion-cycle-ui/Cycle.stproj"); Pump(window.SaveAtAsync(file)); Pump(window.LoadAtAsync(file));
            if (!window.FindControl<TextBox>("MotionRealtimeStatus")!.Text!.Contains("未核验")) throw new Exception("FAIL: project switch retained obsolete verification");
            foreach (var failed in new[] { false, true })
            {
                var completion = new TaskCompletionSource<DeploymentResponse>();
                Func<SshTarget, DeploymentRequest, Task<DeploymentResponse>> read = (_, _) => completion.Task;
                var pending = (Task)Method("RefreshMotionRealtimeCoreAsync").Invoke(window, [read])!;
                if (failed) Pump(window.LoadAtAsync(file));
                else Method("ApplyMotionCycleConfiguration").Invoke(window, [previous with { PeriodMicroseconds = 1000 }]);
                var cleared = window.FindControl<TextBox>("MotionRealtimeStatus")!.Text;
                if (failed) completion.SetException(new IOException("old probe failure"));
                else completion.SetResult(new(true, "diagnostic", "fixture", "", null, null, Realtime: report.RootElement.Clone()));
                Pump(pending);
                if (window.FindControl<TextBox>("MotionRealtimeStatus")!.Text != cleared) throw new Exception("FAIL: late diagnostic overwrote changed project/configuration status");
            }
            if (Environment.GetEnvironmentVariable("ST_CYCLE_DEVICE_VERIFY") == "1")
            {
                Pump((Task)Method("RefreshMotionRealtimeAsync").Invoke(window, null)!);
                var live = window.FindControl<TextBox>("MotionRealtimeStatus")!.Text!;
                if (!live.Contains("设备内核：6.") || !live.Contains("TASK_CYCLIC") || !live.Contains("SCHED_OTHER")) throw new Exception("FAIL: actual device read-only realtime diagnostics missing");
                taskPane.Offset = new Vector(0, 380); Dispatcher.UIThread.RunJobs();
                using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/st-workbench-motion-cycle-device.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                Console.WriteLine("PASS real device diagnostic through desktop SSH response parsing, PLC thread status and unavailable motion metrics; no device writes");
            }
            Console.WriteLine("PASS motion cycle UI apply/rollback, explicit unapplied status, readiness gate, diagnostic display and load reset");
        }
        finally { window.Close(); }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Cycle UI operation timed out"); task.GetAwaiter().GetResult();
    }
}

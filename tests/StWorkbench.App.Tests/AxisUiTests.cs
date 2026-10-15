using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.Toolchain;

public static class AxisUiTests
{
    public static void Run()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var project = (StProject)Field("project").GetValue(window)!;
            var library = window.FindControl<TreeView>("InstructionLibrary")!;
            if (((IEnumerable<TreeViewItem>)library.ItemsSource!).SelectMany(g => (IEnumerable<TreeViewItem>)g.ItemsSource!).Any(n => n.Tag is StInstruction s && (s.ReferenceOnly || s.Code.Contains("待接入")))) throw new Exception("FAIL: empty-axis project still exposes legacy motion comments");
            if (!window.FindControl<TextBlock>("AxisInstructionHint")!.Text!.Contains("配置工程轴")) throw new Exception("FAIL: empty-axis project needs configuration guidance");
            var unchanged = window.FindControl<TextEditor>("SourceEditor")!.Text;
            try { window.InsertInstruction("MC_MoveAbsolute", unchanged.IndexOf("END_PROGRAM", StringComparison.Ordinal)); throw new Exception("FAIL: stale legacy axis template inserted"); }
            catch (InvalidOperationException error) { if (!error.Message.Contains("工程轴")) throw; }
            if (window.FindControl<TextEditor>("SourceEditor")!.Text != unchanged) throw new Exception("FAIL: missing-axis insertion changed source");
            project.Hardware.Axes.Add(new("X", "Axis1", "mm", -100, 100));
            Method("RefreshHardware").Invoke(window, null);
            var tree = (TreeView)Field("tree").GetValue(window)!;
            var hardware = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).Single(g => Equals(g.Header, "硬件"));
            if (!((IEnumerable<TreeViewItem>)hardware.ItemsSource!).Any(n => Equals(n.Header, "轴资源 (1)"))) throw new Exception("FAIL: engineering axis count is stale");
            var search = window.FindControl<TextBox>("InstructionSearch")!; search.Text = "MC_MoveAbsolute"; Dispatcher.UIThread.RunJobs();
            var match = ((IEnumerable<TreeViewItem>)library.ItemsSource!).SelectMany(g => (IEnumerable<TreeViewItem>)g.ItemsSource!).Single();
            if (match.Tag is not StInstruction { ReferenceOnly: false, Id: "Axis_X_MC_MoveAbsolute" }) throw new Exception("FAIL: configured axis must expose executable template, not comment");
            var editor = window.FindControl<TextEditor>("SourceEditor")!;
            window.InsertInstruction("Axis_X_MC_Power", editor.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal));
            var before = editor.Text;
            window.InsertInstruction("Axis_X_MC_MoveAbsolute", editor.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal));
            if (!editor.Text.Contains("Axis := AXIS_X") || !editor.Text.Contains(" : MC_MoveAbsolute;") || editor.Text.Contains("轴控接口待接入")) throw new Exception("FAIL: executable axis invocation not generated");
            editor.Document.UndoStack.Undo(); if (editor.Text != before) throw new Exception("FAIL: axis instance and invocation undo separately");
            window.InsertInstruction("Axis_X_MC_MoveAbsolute", editor.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal));
            window.InsertInstruction("Axis_X_MC_ReadActualPosition", editor.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal));
            if (Environment.GetEnvironmentVariable("ST_AXIS_UI_VERIFY") == "1")
            {
                Pump((Task)Method("CompileAsync").Invoke(window, null)!);
                if (!window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("ST 编译成功")) throw new Exception("FAIL: normal check omitted axis library");
                Pump(window.GeneratePackageAsync());
                if (!window.FindControl<Button>("DownloadButton")!.IsEnabled) throw new Exception("FAIL: UI axis candidate generation");
            }
            else
            {
                Field("candidatePackage").SetValue(window, new PackageResult(new(true, [], "", "", "", ""), "fixture", new string('A', 64), ""));
                var sources = ((Dictionary<Guid, SourceDocument>)Field("sources").GetValue(window)!).Values.ToArray();
                var snapshot = ProjectValidator.Capture(project, sources);
                Field("candidateSourceHash").SetValue(window, snapshot.SourceHash); Field("candidateHardwareHash").SetValue(window, snapshot.HardwareHash);
            }
            Method("ApplyCompiledCandidate").Invoke(window, [new DeploymentResponse(true, "compiled", "fixture", "", null, null, AxisReady: false, AxisMessage: "unconfigured")]);
            if (!window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("基础轴控后端未就绪，不可激活")) throw new Exception("FAIL: basic-axis deployment gate absent");
            search.Text = ""; Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/st-workbench-axis-control.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Console.WriteLine("PASS executable engineering-axis templates, static mapping, grouped undo, ordinary compile/package and unready gate");
        }
        finally { window.Close(); }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static void Pump(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Axis UI operation timeout"); task.GetAwaiter().GetResult();
    }
}

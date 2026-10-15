using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class DownloadApplyUiTests
{
    private static readonly string Candidate = new('A', 64), Old = new('B', 64);
    public static void Run()
    {
        var probe = new MainWindow(); probe.Show();
        try
        {
            if (!Equals(probe.FindControl<Button>("DownloadButton")!.Content, "下载 / 应用程序")) throw new Exception("FAIL: download must describe automatic application");
            if (probe.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "显式激活"))) throw new Exception("FAIL: obsolete explicit activation terminology");
        }
        finally { Field("allowClose").SetValue(probe, true); probe.Close(); }
        Scenario(accept: true);
        Scenario(accept: false);
        Scenario(accept: true, editDuringPrompt: true);
        Scenario(accept: true, targetDuringPrompt: true);
        Scenario(accept: true, alreadyActive: true);
        Scenario(accept: true, blockedMechanics: true);
        Scenario(accept: true, activationUnknown: true);
        Console.WriteLine("PASS download/apply toolbar, actual restart prompt, cancellation, old program stop, identity checks and verified STOP state");
    }
    private static void Scenario(bool accept, bool editDuringPrompt = false, bool targetDuringPrompt = false, bool alreadyActive = false, bool blockedMechanics = false, bool activationUnknown = false)
    {
        var actions = new List<string>(); var state = "running"; var active = alreadyActive ? Candidate : Old;
        Task<DeploymentResponse> Transport(SshTarget target, DeploymentRequest request)
        {
            actions.Add(request.Action);
            if (target.Host != "192.168.1.31") throw new Exception("FAIL: download changed target");
            if (request.Action == "stop") { if (request.ExpectedActiveBuildHash != Old) throw new Exception("FAIL: stop lost baseline"); state = "stopped"; }
            if (request.Action == "activate") { if (state != "stopped" || request.ExpectedActiveBuildHash != Old) throw new Exception("FAIL: activation without stopped baseline"); active = Candidate; state = "activated-not-running"; }
            if (activationUnknown && request.Action == "activate") return Task.FromResult(new DeploymentResponse(false, "unknown", request.RequestId, "uncertain activation", null, null));
            var resultState = request.Action switch { "stage" => "staged", "compile" => "compiled", "status" when state == "activated-not-running" => "stopped", _ => state };
            return Task.FromResult(new DeploymentResponse(true, resultState, request.RequestId, "", JsonSerializer.SerializeToElement(new { build_hash = active }), null, AxisReady: true));
        }
        var constructor = typeof(MainWindow).GetConstructor([typeof(Func<SshTarget, DeploymentRequest, Task<DeploymentResponse>>)]) ?? throw new Exception("FAIL: deployment transport seam missing");
        var window = (MainWindow)constructor.Invoke([(Func<SshTarget, DeploymentRequest, Task<DeploymentResponse>>)Transport]); window.Show();
        try
        {
            ((TextBox)Field("sshHost").GetValue(window)!).Text = "192.168.1.31";
            var project = (StProject)Field("project").GetValue(window)!;
            if (blockedMechanics) project.Hardware.Axes.Add(new("X", "Axis1", "mm", -100, 100) { Mechanics = new() });
            var sources = ((Dictionary<Guid, SourceDocument>)Field("sources").GetValue(window)!).Values.ToArray();
            var snapshot = ProjectValidator.Capture(project, sources);
            var packagePath = Path.GetFullPath("artifacts/download-ui/program.zip"); Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!); File.WriteAllBytes(packagePath, [1, 2, 3]);
            Field("candidatePackage").SetValue(window, new PackageResult(new(true, [], "", "", "", ""), packagePath, Candidate, ""));
            Field("candidateSourceHash").SetValue(window, snapshot.SourceHash); Field("candidateHardwareHash").SetValue(window, snapshot.HardwareHash);
            var operation = (Task)typeof(MainWindow).GetMethod("StageAndCompileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!operation.IsCompleted && !window.OwnedWindows.Any() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
            if (blockedMechanics)
            {
                Pump(operation);
                if (window.OwnedWindows.Any() || !actions.SequenceEqual(new[] { "stage", "compile" }) || !window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("机械与回零参数尚未应用")) throw new Exception("FAIL: automatic apply bypasses offline mechanical gate: " + string.Join(",", actions) + " / " + window.FindControl<TextBlock>("BuildStatus")!.Text + " / dialogs=" + window.OwnedWindows.Count());
                return;
            }
            if (!alreadyActive)
            {
                var dialog = window.OwnedWindows.SingleOrDefault() ?? throw new Exception("FAIL: download must prompt before stopping/restarting core");
                Dispatcher.UIThread.RunJobs();
                var text = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                if (!text.Contains("重启 Runtime 核心") || !text.Contains("保持停止") || !text.Contains("192.168.1.31")) throw new Exception("FAIL: restart prompt omits consequence/target");
                if (actions.Any(a => a is "stop" or "activate")) throw new Exception("FAIL: old program interrupted before confirmation");
                if (accept && !editDuringPrompt && !targetDuringPrompt && !activationUnknown)
                    using (var frame = dialog.CaptureRenderedFrame()) frame?.Save("artifacts/download-restart-dialog.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                if (editDuringPrompt) window.FindControl<TextEditor>("SourceEditor")!.Text += "\n(* changed during confirmation *)";
                if (targetDuringPrompt) ((TextBox)Field("sshHost").GetValue(window)!).Text = "192.168.1.99";
                dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, accept ? "重启核心并应用" : "取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            if (activationUnknown)
            {
                try { Pump(operation); throw new Exception("FAIL: uncertain activation claimed success"); } catch (InvalidOperationException) { }
                if (actions[^1] != "activate" || actions.Count(a => a == "activate") != 1 || window.FindControl<Button>("RunButton")!.IsEnabled || (bool)Field("remoteKnown").GetValue(window)!) throw new Exception("FAIL: uncertain activation retried or retained run permission");
                active = Old; state = "stopped";
                Pump(window.RefreshDeploymentStatusAsync());
                if (!((Button)Field("activateButton").GetValue(window)!).IsEnabled || actions.Count(a => a == "stage") != 1) throw new Exception("FAIL: explicit status refresh must restore retained compiled-program application");
            }
            else if (editDuringPrompt || targetDuringPrompt)
            {
                try { Pump(operation); throw new Exception("FAIL: changed candidate/target applied"); } catch (InvalidOperationException) { }
                if (actions.Any(a => a is "stop" or "activate")) throw new Exception("FAIL: changed identity still caused control write");
            }
            else
            {
                Pump(operation);
                var expected = alreadyActive || !accept ? new[] { "stage", "compile", "status" } : new[] { "stage", "compile", "status", "status", "stop", "activate", "status" };
                if (!actions.SequenceEqual(expected)) throw new Exception("FAIL: unexpected download action sequence: " + string.Join(",", actions));
                if (accept && !alreadyActive && (!window.FindControl<Button>("RunButton")!.IsEnabled || !window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("已停止"))) throw new Exception("FAIL: successful download must report verified STOP and allow separate Run");
            }
        }
        finally { Field("allowClose").SetValue(window, true); window.Close(); }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult();
    }
}

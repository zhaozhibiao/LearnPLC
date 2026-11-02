using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.Toolchain;

public static class EtherCatRuntimeUiTests
{
    public static void Run()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            window.FindControl<TabControl>("WorkbenchTabs")!.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var entry = window.FindControl<Button>("ApplyEtherCatHardwareButton") ?? throw new Exception("FAIL: hardware page has no discoverable apply entry");
            if (entry.GetVisualAncestors().Any(c => c is Expander or ScrollViewer)) throw new Exception("FAIL: hardware apply entry hidden in collapsed or scrollable configuration");
            if (!entry.IsEffectivelyVisible || !Equals(entry.Content, "下载硬件")) throw new Exception("FAIL: hardware download entry not visible on hardware page");
            if (window.FindControl<CheckBox>("GenericEtherCatEnabled") is not { IsChecked: false }) throw new Exception("FAIL: generic runtime must be visible and opt-in");
            if (window.FindControl<ComboBox>("StationRuntimeProfile") is null || window.FindControl<ComboBox>("StationDcMode") is null) throw new Exception("FAIL: station PDO/DC/profile settings missing");
            if (window.FindControl<CheckBox>("RuntimeMechanicsConfirmed") is not { IsChecked: false } || window.FindControl<CheckBox>("RuntimeSafetyConfirmed") is not { IsChecked: false }) throw new Exception("FAIL: runtime commissioning cannot be preconfirmed");
            window.FindControl<CheckBox>("GenericEtherCatEnabled")!.IsChecked = true;
            window.FindControl<TextBox>("EtherCatCycleMicroseconds")!.Text = "1000";
            window.ApplyEtherCatRuntimeSettings();
            window.CheckEtherCatRuntimeSettings();
            if (window.FindControl<TextBlock>("EtherCatRuntimeStatus")!.Text?.Contains("未满足") != true) throw new Exception("FAIL: empty topology marked ready");
            Console.WriteLine("PASS: generic EtherCAT UI opt-in, station options, confirmations and blocked empty topology");
        }
        finally { window.Close(); }
        foreach (var scenario in new[] { "apply", "entry", "cancel", "edit", "target", "candidate" }) HardwareApplication(scenario);
        Console.WriteLine("PASS: EtherCAT application requires restart confirmation, no password prompt, and preserves candidate/target guards and stopped controls");
        ProgramActivation(true);
        ProgramActivation(false);
        Console.WriteLine("PASS generic mechanical candidate activation requires actual axis readiness across result, button and download gates");
    }
    private static void ProgramActivation(bool axisReady)
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var load = new ProjectStore().LoadAsync(Path.GetFullPath("examples/is620n-turn-test/TurnTest.stproj"));
            PumpUntil(() => load.IsCompleted);
            var loaded = load.GetAwaiter().GetResult();
            Field("project").SetValue(window, loaded.Project);
            Field("sources").SetValue(window, loaded.Sources.ToDictionary(s => s.ItemId));
            var snapshot = ProjectValidator.Capture(loaded.Project, loaded.Sources);
            var package = new PackageResult(new(true, [], "", "", "", ""), "fixture.zip", new string('C', 64), "");
            Field("candidatePackage").SetValue(window, package);
            Field("candidateSourceHash").SetValue(window, snapshot.SourceHash);
            Field("candidateHardwareHash").SetValue(window, snapshot.HardwareHash);
            Field("remoteKnown").SetValue(window, true);
            Field("activeBuildHash").SetValue(window, new string('A', 64));
            typeof(MainWindow).GetMethod("ApplyCompiledCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                [new DeploymentResponse(true, "compiled", "fixture", "", null, null, AxisReady: axisReady, AxisMessage: "Loaded native hardware identity differs: ethercatTopologyFingerprint")]);
            if ((bool)Field("motionDeploymentReady").GetValue(window)! != axisReady) throw new Exception("FAIL: generic mechanics still blocked despite verified native axis readiness");
            typeof(MainWindow).GetMethod("RefreshCompiledProgramAction", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            if (!((Button)Field("activateButton").GetValue(window)!).IsEnabled) throw new Exception("FAIL: compiled generic candidate must allow checked configuration preparation");
            var hostType = typeof(MainWindow).GetNestedType("DownloadHost", BindingFlags.NonPublic)!;
            var host = (IProgramDownloadHost)Activator.CreateInstance(hostType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [window, new Func<bool>(() => true), new SshTarget("192.168.1.31", "a")], null)!;
            if (host.CanApply != axisReady) throw new Exception("FAIL: download host still blocks verified generic mechanics");
            if (!axisReady && window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("缺少参数应用")) throw new Exception("FAIL: actual hardware mismatch hidden by obsolete interface message");
            if (!axisReady && !window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("总线组态")) throw new Exception("FAIL: hardware mismatch needs actionable configuration status");
        }
        finally { Field("allowClose").SetValue(window, true); window.Close(); }
    }
    private static void HardwareApplication(string scenario)
    {
        var actions = new List<string>();
        Task<DeploymentResponse> Transport(SshTarget target, DeploymentRequest request)
        {
            actions.Add(request.Action);
            if (request.Action == "hardware-apply" && request.ExpectedActiveBuildHash != "baseline") throw new Exception("FAIL: hardware apply lost active identity");
            var state=request.Action switch { "hardware-stage"=>"staged","hardware-validate"=>"hardware-validated","hardware-apply"=>"hardware-applied",_=>"stopped" };
            return Task.FromResult(new DeploymentResponse(true, state, request.RequestId, "fixture", JsonSerializer.SerializeToElement(new { build_hash = "baseline" }), null,HardwareMatchesActive:false));
        }
        var window = new MainWindow(Transport); window.Show();
        try
        {
            ((TextBox)Field("sshHost").GetValue(window)!).Text = "192.168.1.31";
            var project = (StProject)Field("project").GetValue(window)!;
            const string xml = "<EtherCATInfo><Vendor><Id>1</Id></Vendor><Descriptions><Devices><Device><Type ProductCode='2' RevisionNo='3'>D</Type><Name>D</Name><Sm>MBoxOut</Sm><Sm>MBoxIn</Sm><Sm Watchdog='1'>Outputs</Sm><Sm>Inputs</Sm><RxPdo Sm='2' Fixed='1'><Index>#x1600</Index><Name>Out</Name><Entry><Index>#x7000</Index><SubIndex>1</SubIndex><BitLen>16</BitLen><DataType>UINT</DataType></Entry></RxPdo></Device></Devices></Descriptions></EtherCATInfo>";
            var device = EsiImporter.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Single();
            project.Hardware.Runtime = new(); project.Hardware.Devices = [device];
            project.Hardware.Stations = [new(Guid.NewGuid(), "IO", device.Id, 0, device.Entries)];
            var sources = ((Dictionary<Guid, SourceDocument>)Field("sources").GetValue(window)!).Values.ToArray();
            var snapshot = ProjectValidator.Capture(project, sources);
            var packagePath = Path.GetFullPath("artifacts/hardware-ui/program.zip"); Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!); File.WriteAllBytes(packagePath, [1, 2, 3]);
            var package = new PackageResult(new(true, [], "", "", "", ""), packagePath, new string('A', 64), "");
            Field("candidatePackage").SetValue(window, package);
            Field("candidateSourceHash").SetValue(window, snapshot.SourceHash); Field("candidateHardwareHash").SetValue(window, snapshot.HardwareHash);
            Field("compiledBuildHash").SetValue(window, package.BuildHash);
            ((Button)Field("activateButton").GetValue(window)!).IsEnabled = true;
            ((Button)Field("startButton").GetValue(window)!).IsEnabled = true;
            window.FindControl<TextEditor>("SourceEditor")!.Text="PROGRAM Main\nVAR\nEND_VAR\nTHIS IS INVALID ST;\nEND_PROGRAM";
            async Task AwaitEntryAsync() { do { await Task.Delay(1); } while ((bool)Field("deploymentBusy").GetValue(window)!); }
            Task operation;
            if (scenario == "entry")
            {
                window.FindControl<Button>("ApplyEtherCatHardwareButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                operation = AwaitEntryAsync();
            }
            else operation = (Task)typeof(MainWindow).GetMethod("DownloadHardwareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            PumpUntil(() => operation.IsCompleted || window.OwnedWindows.Any());
            if (operation.IsCompleted) operation.GetAwaiter().GetResult();
            var dialog = window.OwnedWindows.SingleOrDefault() ?? throw new Exception("FAIL: hardware restart confirmation missing");
            Dispatcher.UIThread.RunJobs();
            if (!string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)).Contains("重启运动核心")) throw new Exception("FAIL: hardware restart consequence missing");
            if (!actions.SequenceEqual(new[] { "hardware-stage", "status", "hardware-validate" })) throw new Exception("FAIL: hardware applied before confirmation");
            if (scenario == "edit") window.FindControl<TextEditor>("SourceEditor")!.Text += "\n(* hardware confirmation edit *)";
            if (scenario == "target") ((TextBox)Field("sshHost").GetValue(window)!).Text = "192.168.1.99";
            if (scenario == "candidate") Field("candidatePackage").SetValue(window, package with { BuildHash = new string('B', 64) });
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, scenario == "cancel" ? "取消" : "下载硬件")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => operation.IsCompleted || window.OwnedWindows.Any());
            if (window.OwnedWindows.Any()) throw new Exception("FAIL: hardware application requested additional administrator password");
            if (scenario is "edit" or "target")
            {
                try { operation.GetAwaiter().GetResult(); throw new Exception("FAIL: changed hardware identity accepted"); } catch (InvalidOperationException) { }
            }
            else operation.GetAwaiter().GetResult();
            if (actions.Contains("hardware-apply") != (scenario is "apply" or "entry" or "candidate")) throw new Exception("FAIL: unexpected hardware application");
            if ((bool)Field("deploymentBusy").GetValue(window)!) throw new Exception("FAIL: hardware application busy flag retained");
            if (scenario is "apply" or "entry" && (((Button)Field("activateButton").GetValue(window)!).IsEnabled || ((Button)Field("startButton").GetValue(window)!).IsEnabled || Field("compiledBuildHash").GetValue(window) is not null)) throw new Exception("FAIL: hardware application retained stale run permission");
        }
        finally { Field("allowClose").SetValue(window, true); foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void PumpUntil(Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!complete() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!complete()) throw new TimeoutException();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.OpenPlc;
using StWorkbench.Toolchain;

try
{
AppBuilder.Configure<WorkbenchApplication>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var originalSettings = WorkbenchSettings.Load();
var realDevice = Environment.GetEnvironmentVariable("ST_REAL_APP_VERIFY") == "1";
if (realDevice) (originalSettings with { Endpoint = "https://localhost:18443" }).Save();
var window = new MainWindow();
if (Environment.GetEnvironmentVariable("ST_DOWNLOAD_APPLY_UI_VERIFY") == "1") { DownloadApplyUiTests.Run(); window.Close(); return; }
if (Environment.GetEnvironmentVariable("ST_ENGINEERING_NAVIGATION_UI_VERIFY") == "1") { EngineeringNavigationUiTests.Run(); window.Close(); return; }
if (Environment.GetEnvironmentVariable("ST_IO_MAPPING_UI_VERIFY") == "1") { IoMappingUiTests.Run(); window.Close(); return; }
if (Environment.GetEnvironmentVariable("ST_PATH_NAVIGATION_UI_VERIFY") == "1") { PathNavigationUiTests.Run(); window.Close(); return; }
if (Environment.GetEnvironmentVariable("ST_AXIS_PROPERTIES_UI_VERIFY") == "1") { AxisPropertiesUiTests.Run(); window.Close(); return; }
if (Environment.GetEnvironmentVariable("ST_AXIS_UI_VERIFY") == "1") { AxisUiTests.Run(); window.Close(); return; }
if (Environment.GetEnvironmentVariable("ST_MULTI_AXIS_UI_VERIFY") == "1") { MultiAxisUiTests.Run(); window.Close(); return; }
if (Environment.GetEnvironmentVariable("ST_PATH_SCALE_UI_VERIFY") == "1") { PathScaleUiTests.Run(); window.Close(); return; }
if (Environment.GetEnvironmentVariable("ST_RESOURCE_COMPILE_VERIFY") == "1") { ResourceDragTests.Run(); window.Close(); return; }
if (Environment.GetEnvironmentVariable("ST_CYCLE_COMPILE_VERIFY") == "1" || Environment.GetEnvironmentVariable("ST_CYCLE_DEVICE_VERIFY") == "1") { MotionCycleUiTests.Run(); window.Close(); return; }
if (realDevice) originalSettings.Save();
window.Show();
Dispatcher.UIThread.RunJobs();
if (Environment.GetEnvironmentVariable("ST_REAL_EXISTING_MONITOR_VERIFY") == "1")
{
    var packagePath = Path.GetFullPath("artifacts/packages-build/5d8c454c5672433d9d3255fd36779c8b/program.zip");
    using var zip = System.IO.Compression.ZipFile.OpenRead(packagePath);
    using var manifestStream = zip.GetEntry("build-manifest.json")!.Open();
    using var manifest = System.Text.Json.JsonDocument.Parse(manifestStream);
    using var input = System.Text.Json.JsonDocument.Parse(File.ReadAllText("artifacts/packages-build/5d8c454c5672433d9d3255fd36779c8b/request.json"));
    var documents = System.Text.Json.JsonSerializer.Deserialize<SourceDocument[]>(input.RootElement.GetProperty("Sources"))!;
    var tasks = System.Text.Json.JsonSerializer.Deserialize<List<PlcTask>>(input.RootElement.GetProperty("Tasks"))!;
    var project = new StProject { Id = manifest.RootElement.GetProperty("ProjectId").GetGuid(), Name = "ST 示例工程", Tasks = tasks,
        Items = documents.Select(d => new ProjectItem { Id=d.ItemId, SourcePath=d.RelativePath, Name=input.RootElement.GetProperty("ProgramNames").GetProperty(d.ItemId.ToString()).GetString()! }).ToList() };
    var snapshot = ProjectValidator.Capture(project, documents);
    if (snapshot.SourceHash != manifest.RootElement.GetProperty("SourceHash").GetString() || snapshot.HardwareHash != manifest.RootElement.GetProperty("HardwareHash").GetString())
        throw new Exception("Existing package source identity mismatch; monitor refused");
    var projectPath = Path.GetFullPath("artifacts/monitor-existing-project/Current.stproj");
    Pump(new ProjectStore().SaveAsync(projectPath, project, documents)); Pump(window.LoadAtAsync(projectPath));
    Pump(window.GeneratePackageAsync());
    var rebuilt = (PackageResult)typeof(MainWindow).GetField("candidatePackage", System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
    if (rebuilt.BuildHash != manifest.RootElement.GetProperty("BuildHash").GetString()) throw new Exception("Rebuilt current project differs from active package");
    try
    {
    var sourceBeforeMonitor = window.FindControl<TextEditor>("SourceEditor")!.Text;
    Pump(window.ConnectPrivateRuntimeAsync()); Pump(window.BindDebugAsync());
    for (int i=0;i<3;i++) { Pump(window.PollVariablesAsync()); Thread.Sleep(200); }
    if (!window.FindControl<TextBox>("MonitorStatus")!.Text!.Contains("真实只读采样") || window.FindControl<TreeView>("InstanceTree")!.ItemCount == 0 || window.FindControl<TrendView>("TrendView")!.Points.Count < 2)
        throw new Exception("Existing active program UI monitor failed");
    window.FindControl<TabControl>("WorkbenchTabs")!.SelectedIndex = 5; Dispatcher.UIThread.RunJobs();
    using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/current-program-monitor.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    window.FindControl<TabControl>("WorkbenchTabs")!.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
    var inline = (InlineMonitorGenerator)typeof(MainWindow).GetField("inlineMonitor", System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
    if (inline.Values.Count < 3 || inline.Values.Any(v => v.Quality != SampleQuality.Good) || !inline.Values.Any(v => v.Text == "TRUE") || window.FindControl<TextEditor>("SourceEditor")!.Text != sourceBeforeMonitor)
        throw new Exception("ST editor must show actual PLC values without changing source");
    using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/st-editor-online.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    var timer = (DispatcherTimer)typeof(MainWindow).GetField("monitorTimer", System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
    timer.Stop(); Thread.Sleep(2100);
    typeof(MainWindow).GetMethod("RefreshInlineMonitor", System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
    if (inline.Values.Any(v => v.Quality != SampleQuality.Expired || v.Text != "已过期")) throw new Exception("Stale editor values must visibly expire");
    Pump(window.PollVariablesAsync());
    var activeClient = (DebugSocketClient)typeof(MainWindow).GetField("debugClient", System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
    Pump(activeClient.DisposeAsync().AsTask());
    try { Pump(window.PollVariablesAsync()); throw new Exception("Disposed debug connection unexpectedly sampled"); }
    catch (ObjectDisposedException) { }
    if (!window.FindControl<TextBox>("MonitorStatus")!.Text!.Contains("断开原因")) throw new Exception("Sampling failure reason was overwritten by generic disconnect message");
    if (inline.Values.Any(v => v.Quality == SampleQuality.Good || v.Text != "不可用")) throw new Exception("Disconnected ST inlays retained live values");
    Pump(window.BindDebugAsync());
    if (!inline.Values.Any(v => v.Quality == SampleQuality.Good)) throw new Exception("Rebind did not restore real editor values");
    window.FindControl<TextEditor>("SourceEditor")!.Text += "\n(* edited offline *)";
    if (inline.Values.Any(v => v.Quality == SampleQuality.Good)) throw new Exception("Edited source retained valid inlays");
    window.FindControl<TextEditor>("SourceEditor")!.Text = sourceBeforeMonitor;
    Pump(window.SaveAtAsync(projectPath));
    }
    finally { window.Close(); }
    Console.WriteLine("PASS current active program matched source/package, real ST editor values without source mutation, offline/edit invalidation, instance values and trends, sampling failure reason retained, no device writes"); return;
}
if (Environment.GetEnvironmentVariable("ST_REAL_CONNECTION_VERIFY") == "1")
{
    Pump(window.LoadAtAsync(Path.GetFullPath("examples/counter/Counter.stproj")));
    Pump(window.ConnectPrivateRuntimeAsync());
    var details = (TextBox)typeof(MainWindow).GetField("runtimeDetails", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
    if (details is null || !details.Text!.Contains("STOPPED")) throw new Exception("FAIL: private connection did not confirm actual stopped Runtime");
    if (window.FindControl<Button>("RunButton") is not { IsEnabled: true }) throw new Exception("FAIL: confirmed stopped active program must expose enabled Run");
    var endpointField = typeof(MainWindow).GetField("privateRuntimeEndpoint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var firstEndpoint = (Uri)endpointField.GetValue(window)!;
    Pump(window.ConnectPrivateRuntimeAsync());
    var secondEndpoint = (Uri)endpointField.GetValue(window)!;
    if (firstEndpoint.Port == secondEndpoint.Port) throw new Exception("FAIL: reconnect reused prior forwarding port");
    AssertPortClosed(firstEndpoint.Port);
    window.FindControl<TabControl>("WorkbenchTabs")!.SelectedIndex = 5;
    Dispatcher.UIThread.RunJobs();
    using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/st-workbench-connected.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    window.Close();
    AssertPortClosed(secondEndpoint.Port);
    Console.WriteLine("PASS: ordinary launch, password-free SSH login, actual STOPPED status, reconnect and window-close tunnel cleanup");
    return;
}
if (window.FindControl<TextEditor>("SourceEditor") is not { } editor || !editor.Text.Contains("PROGRAM Main")) throw new Exception("FAIL: initial ST editor");
if (Environment.GetEnvironmentVariable("ST_PATH_UI_COMPILE_VERIFY") == "1")
{
    Pump(window.LoadAtAsync(Path.GetFullPath("examples/continuous-path/Continuous.stproj")));
    Pump((Task)typeof(MainWindow).GetMethod("CompileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!);
    if (!window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("ST 编译成功")) throw new Exception("Path program ordinary check/compile did not include motion library");
    Pump(window.GeneratePackageAsync());
    typeof(MainWindow).GetMethod("ApplyCompiledCandidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [new DeploymentResponse(true, "compiled", "fixture", "", null, null, MotionReady: false, MotionMessage: "unconfigured")]);
    if (!window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("不可激活")) throw new Exception("Missing continuous backend must visibly block activation before confirmation");
    window.Close(); Console.WriteLine("PASS path UI normal compiler command, candidate generation and unready backend activation gate"); return;
}
if (Environment.GetEnvironmentVariable("ST_INSTRUCTION_COMPILE_VERIFY") == "1")
{
    var source = "PROGRAM Main\nVAR\nEND_VAR\nEND_PROGRAM\n";
    foreach (var instruction in StInstructionCatalog.All.Where(s => !s.ReferenceOnly)) source = StSnippetInsertion.Insert(source, source.IndexOf("END_PROGRAM", StringComparison.Ordinal), instruction).Text;
    editor.Text = source;
    Pump(window.GeneratePackageAsync());
    var package = typeof(MainWindow).GetField("candidatePackage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window);
    if (package is null) throw new Exception("FAIL: standard instruction examples did not compile: " + window.FindControl<TextBlock>("BuildStatus")!.Text);
    Pump(window.SaveAtAsync(Path.GetFullPath("artifacts/instruction-example/Instructions.stproj"))); window.Close();
    Console.WriteLine("PASS all standard library snippets compiled and packaged by real STruC++ compiler"); return;
}
    UsabilityTests.Run(window);
    ResourceDragTests.Run();
    MotionCycleUiTests.Run();
    AxisUiTests.Run();
    ConfigurationUiTests.Run();
    IoMappingUiTests.Run();
    AxisPropertiesUiTests.Run();
    MultiAxisUiTests.Run();
    PathScaleUiTests.Run();
    PathNavigationUiTests.Run();
    EngineeringNavigationUiTests.Run();
    DownloadApplyUiTests.Run();
PathUiTests.Run(window);
if (window.FindControl<Button>("StartInlineMonitor") is null || window.FindControl<ComboBox>("MonitorInstance") is null || !window.FindControl<TextBlock>("InlineMonitorStatus")!.Text!.Contains("不可用")) throw new Exception("FAIL: ST editor online controls and truthful initial unavailable status");
if (window.FindControl<TreeView>("ProjectTree") is not { ItemCount: > 0 }) throw new Exception("FAIL: project tree");
if (window.FindControl<Button>("DownloadButton") is not { IsEnabled: false }) throw new Exception("FAIL: unverified download must remain unavailable");
try { Pump(window.BindDebugAsync()); throw new Exception("FAIL: monitoring without authentication must fail"); }
catch (InvalidOperationException e) when (e.Message.Contains("请先通过 SSH")) { }
if (!window.FindControl<TextBox>("MonitorStatus")!.Text!.Contains("请先通过 SSH"))
    throw new Exception("FAIL: monitoring failure reason must appear beside the monitor controls");
if (window.FindControl<Button>("RunButton") is not { IsEnabled: false } runButton || runButton.Parent is not WrapPanel)
    throw new Exception("FAIL: top toolbar must expose disabled Run until device status is confirmed");
if (window.FindControl<Button>("StopButton") is not { IsEnabled: false }) throw new Exception("FAIL: Stop needs confirmed device identity");
var apply = typeof(MainWindow).GetMethod("ApplyDeploymentResponse", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
using (var identity = System.Text.Json.JsonDocument.Parse("{\"build_hash\":\"" + new string('a',64) + "\"}"))
{
    void State(string state, bool canRun, bool canStop)
    {
        apply.Invoke(window, new object[] { new StWorkbench.Toolchain.DeploymentResponse(true, state, "test", "", identity.RootElement.Clone(), null), "status" });
        if (runButton.IsEnabled != canRun || window.FindControl<Button>("StopButton")!.IsEnabled != canStop)
            throw new Exception("FAIL: run/stop state gating: " + state);
    }
    State("activated-not-running", false, false);
    State("stopped", true, false);
    State("running", false, true);
    State("unavailable", false, false);
}
apply.Invoke(window, new object[] { new StWorkbench.Toolchain.DeploymentResponse(true, "unavailable", "test", "", null, null), "status" });
editor.Text += "\n(* edited *)";
Dispatcher.UIThread.RunJobs();
if (window.FindControl<TextBlock>("ProjectStatus") is not { } status || !status.Text!.Contains("未保存")) throw new Exception("FAIL: unsaved change indicator");
var saveDirectory = Path.Combine(Path.GetTempPath(), "st-ui-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(saveDirectory);
    var save = window.SaveAtAsync(Path.Combine(saveDirectory, "Project.stproj"));
    editor.Text += "\n(* concurrent edit *)";
    Pump(save);
    if (!status.Text!.Contains("未保存")) throw new Exception("FAIL: edit during save remains dirty");
    var load = new StWorkbench.Core.ProjectStore().LoadAsync(Path.Combine(saveDirectory, "Project.stproj"));
    Pump(load);
    var saved = load.GetAwaiter().GetResult();
    if (saved.Sources[0].Text.Contains("concurrent edit")) throw new Exception("FAIL: save must preserve captured source version");
    var open = window.LoadAtAsync(Path.Combine(saveDirectory, "Project.stproj"));
    editor.Text += "\n(* edit during load *)";
    try { Pump(open); throw new Exception("FAIL: load overwrote edit"); }
    catch (InvalidOperationException e) when (e.Message.Contains("已保留修改")) { }
    if (!editor.Text.Contains("edit during load")) throw new Exception("FAIL: concurrent edit was lost");
    var buildStatus = window.FindControl<TextBlock>("BuildStatus")!;
    buildStatus.Text = "上一工程编译成功";
    Pump(window.LoadAtAsync(Path.Combine(saveDirectory, "Project.stproj")));
    if (buildStatus.Text != "尚未编译") throw new Exception("FAIL: stale build success survived project replacement");
    Pump(window.LoadAtAsync(Path.Combine(saveDirectory, "Project.stproj")));
    var currentProject = (StProject)typeof(MainWindow).GetField("project", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
    var selectedResource = (ProjectItem)typeof(MainWindow).GetField("selected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
    if (!currentProject.Items.Contains(selectedResource)) throw new Exception("FAIL: unchanged file load retained a resource from the previous project object");
}
finally { Directory.Delete(saveDirectory, true); }
if (realDevice)
{
    Pump(window.LoadAtAsync(Path.GetFullPath("examples/counter/Counter.stproj")));
    Pump(window.GeneratePackageAsync());
    if (!window.FindControl<Button>("DownloadButton")!.IsEnabled) throw new Exception("FAIL: valid current candidate unavailable");
    Pump(window.ConnectPrivateRuntimeAsync()); Pump(window.BindDebugAsync());
    window.FindControl<TabControl>("WorkbenchTabs")!.SelectedIndex = 5;
    var until = DateTime.UtcNow.AddSeconds(2);
    while (DateTime.UtcNow < until) { Pump(window.PollVariablesAsync()); Dispatcher.UIThread.RunJobs(); Thread.Sleep(100); }
    if (!window.FindControl<TextBox>("MonitorStatus")!.Text!.Contains("真实只读采样")) throw new Exception("FAIL: real monitor data unavailable");
    if (window.FindControl<TreeView>("InstanceTree")!.ItemCount == 0 || window.FindControl<TrendView>("TrendView")!.Points.Count < 2) throw new Exception("FAIL: real instance and trend samples unavailable");
    using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/st-workbench-online.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    editor.Text += "\n(* invalidate monitor identity *)";
    Dispatcher.UIThread.RunJobs();
    if (!window.FindControl<TextBox>("MonitorStatus")!.Text!.Contains("过期")) throw new Exception("FAIL: edited source retains current monitor data");
    Console.WriteLine("PASS: real desktop candidate identity, SSH session, device values, instance expansion, trend, edited-source invalidation");
}
Directory.CreateDirectory("artifacts");
using var bitmap = window.CaptureRenderedFrame();
bitmap?.Save("artifacts/st-workbench.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
window.Hide();
Console.WriteLine("PASS: editor, project tree, disabled unsafe download, dirty save, concurrent load, reset build identity, rendered desktop");
}
catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
static void Pump(Task task)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
    if (!task.IsCompleted) throw new TimeoutException("UI operation did not finish");
    task.GetAwaiter().GetResult();
}
static void AssertPortClosed(int port)
{
    using var socket = new System.Net.Sockets.TcpClient();
    try { socket.Connect("127.0.0.1", port); }
    catch (System.Net.Sockets.SocketException) { return; }
    throw new Exception("FAIL: owned forwarding port remains open");
}


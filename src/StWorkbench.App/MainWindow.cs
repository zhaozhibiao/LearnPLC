using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using StWorkbench.Core;
using StWorkbench.OpenPlc;
using StWorkbench.Toolchain;
namespace StWorkbench.App;
public sealed partial class MainWindow : Window
{
    private readonly NameScope scope = new();
    private Control? editorWorkspace;
    private readonly TextEditor editor = new() { ShowLineNumbers = true, FontFamily = new FontFamily("Cascadia Code, Consolas, Microsoft YaHei"), FontSize = 15, Background = Brush.Parse("#111B2A"), Foreground = Brush.Parse("#DBE5F5"), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TreeView tree = new() { Margin = new Thickness(8) };
    private readonly TextBlock projectStatus = new() { FontSize = 12, Foreground = Brush.Parse("#9AAFC9") };
    private readonly TextBlock hint = new() { Text = "Ctrl+Space 补全 · F12 定义 · Ctrl+S 保存 · F7 编译检查", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(12, 7) };
    private readonly TextBlock documentLabel = new() { Text = "Main.st", FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(14, 9) };
    private readonly InlineMonitorGenerator inlineMonitor = new();
    private readonly ComboBox monitorInstance = new() { MinWidth = 210, MaxWidth = 400, PlaceholderText = "选择程序 / 功能块实例" };
    private readonly TextBlock inlineStatus = new() { Text = "离线 · 连接并绑定后在变量旁显示真实值", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4), Foreground = Brush.Parse("#9AAFC9") };
    private bool refreshingInline;
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Microsoft YaHei"), FontSize = 12 };
    private readonly ListBox diagnostics = new();
    private readonly TextBlock buildStatus = new() { Text = "尚未编译", Foreground = Brush.Parse("#9CB0CD"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox endpoint = new() { PlaceholderText = "https://主机:8443" };
    private readonly TextBox username = new();
    private readonly TextBox password = new() { PasswordChar = '●', PlaceholderText = "Runtime 密码（仅本次连接）" };
    private readonly TextBox compilerPath = new();
    private readonly TextBox compilerVersion = new();
    private readonly TextBox certificateFingerprint = new() { PlaceholderText = "可选：通过 SSH 核对的设备证书 SHA256" };
    private readonly TextBox taskPeriod = new() { Text = "10", Width = 120 };
    private readonly TextBox taskName = new() { Text = "Cyclic", Width = 200 };
    private readonly ComboBox taskProgram = new() { Width = 260 };
    private readonly ListBox ioList = new() { MinHeight = 110 };
    private readonly ListBox axisList = new() { MinHeight = 110 };
    private readonly ListBox deviceList = new() { MinHeight = 90 };
    private readonly ListBox pdoList = new() { MinHeight = 90 };
    private readonly TextBox runtimeDetails = new() { AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
    private readonly TextBox monitorExplanation = new() { AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Text = "变量监视尚未绑定。\n通过 SSH 连接 Runtime，生成与已激活程序一致的工程候选包，然后点击下方“绑定已激活程序并开始只读监视”。\n绑定后显示真实变量值、实例树和趋势。" };
    private readonly ProjectStore store = new();
    private readonly StLanguageService language = new();
    private StProject project = new();
    private Dictionary<Guid, SourceDocument> sources = [];
    private ProjectItem? selected;
    private string? projectPath;
    private bool loading, dirty, busy;
    private CancellationTokenSource? buildCancellation;
    private WorkbenchSettings settings = WorkbenchSettings.Load();
    private OpenPlcClient? runtime;
    private IReadOnlyList<Diagnostic> currentDiagnostics = [];
    private string? diagnosticsSourceHash;
    private long editGeneration, connectionGeneration;
    private Button buildButton = null!;
    private Button packageButton = null!;
    private Button connectButton = null!;
    private Button privateConnectButton = null!;
    private SshRuntimeTunnel? privateTunnel;
    private Uri? privateRuntimeEndpoint;
    private bool connecting;
    private Button refreshButton = null!;
    private Button downloadButton = null!, activateButton = null!, startButton = null!, stopButton = null!;
    private readonly TextBlock deviceRunStatus = new() { Text = "设备状态未确认", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox sshHost = new() { Text = "192.168.1.31", Width = 220 };
    private readonly TextBox sshUser = new() { Text = "a", Width = 120 };
    private readonly TextBox deploymentDetails = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140 };
    private PackageResult? candidatePackage;
    private string? candidateSourceHash, candidateHardwareHash, compiledBuildHash, activeBuildHash;
    private bool remoteKnown, deploymentBusy;
    private bool motionDeploymentReady;
    private string? debugSessionToken;
    private DebugSocketClient? debugClient;
    private ReadOnlyMonitorSession? monitorSession;
    private IReadOnlyList<DebugSymbol> debugSymbols = [];
    private bool debugLittleEndian, polling, bindingDebug;
    private readonly TreeView instanceTree = new() { Height = 170 };
    private readonly TrendView trendView = new() { Height = 170 };
    private readonly DispatcherTimer monitorTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private long monitorSequence;
    private string? trendPath;
    private SshTarget? operationTarget;

    public MainWindow()
    {
        NameScope.SetNameScope(this, scope);
        Title = "ST Workbench · 工程站"; Width = 1440; Height = 940; MinWidth = 1080; MinHeight = 720;
        Background = Brush.Parse("#0C1523"); FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Register(editor, "SourceEditor"); Register(tree, "ProjectTree"); Register(projectStatus, "ProjectStatus"); Register(buildStatus, "BuildStatus"); Register(instanceTree, "InstanceTree"); Register(monitorExplanation, "MonitorStatus"); Register(trendView, "TrendView");
        editor.SyntaxHighlighting = StHighlighting.Create(); editor.Options.ConvertTabsToSpaces = true; editor.Options.IndentationSize = 4;
        editor.TextArea.TextView.ElementGenerators.Add(inlineMonitor);
        Register(inlineStatus, "InlineMonitorStatus"); Register(monitorInstance, "MonitorInstance");
        monitorInstance.SelectionChanged += (_, _) => { if (!refreshingInline) RefreshInlineMonitor(); };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 14) };
        header.Children.Add(new StackPanel { Spacing = 3, Children = { new TextBlock { Text = "ST WORKBENCH", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#E5EEFF") }, projectStatus } });
        var badge = new Border { Background = Brush.Parse("#173044"), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 6), Child = new TextBlock { Text = "ST 工程  /  本地开发", Foreground = Brush.Parse("#7ECED4"), FontSize = 12 } }; Grid.SetColumn(badge, 1); header.Children.Add(badge);
        var commands = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 14, 12) };
        commands.Children.Add(ActionButton("新建", () => NewProjectAsync()));
        commands.Children.Add(ActionButton("打开", OpenAsync));
        commands.Children.Add(ActionButton("保存", SaveAsync));
        commands.Children.Add(ActionButton("另存为", SaveAsAsync));
        buildButton = ActionButton("检查 / 编译", CompileAsync); commands.Children.Add(buildButton);
        packageButton = ActionButton("生成候选包", GeneratePackageAsync); commands.Children.Add(packageButton);
        commands.Children.Add(ActionButton("取消编译", () => { buildCancellation?.Cancel(); return Task.CompletedTask; }));
        downloadButton = ActionButton("下载 / Linux 编译", StageAndCompileAsync); downloadButton.IsEnabled = false; Register(downloadButton, "DownloadButton"); commands.Children.Add(downloadButton);
        activateButton = ActionButton("显式激活", ActivateAsync); activateButton.IsEnabled = false; commands.Children.Add(activateButton);
        startButton = ActionButton("▶ 运行", () => DeploymentActionAsync("start")); startButton.IsEnabled = false;
        ToolTip.SetTip(startButton, "运行设备上已激活的程序；需先确认设备处于停止状态。"); Register(startButton, "RunButton"); commands.Children.Add(startButton);
        stopButton = ActionButton("■ 停止", () => DeploymentActionAsync("stop")); stopButton.IsEnabled = false; Register(stopButton, "StopButton"); commands.Children.Add(stopButton);
        commands.Children.Add(ActionButton("刷新设备状态", RefreshDeploymentStatusAsync));
        Register(deviceRunStatus, "DeviceRunStatus"); commands.Children.Add(deviceRunStatus);
        commands.Children.Add(buildStatus);
        foreach (var child in commands.Children) child.Margin = new Thickness(0, 0, 7, 6);
        var center = new Grid { ColumnDefinitions = new ColumnDefinitions("230,5,*,5,290"), Margin = new Thickness(14, 0, 14, 0) };
        var left = new DockPanel();
        var treeHeader = new TextBlock { Text = "工程资源", FontWeight = FontWeight.SemiBold, Margin = new Thickness(12) }; DockPanel.SetDock(treeHeader, Dock.Top); left.Children.Add(treeHeader);
        var create = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(7) };
        var kind = new ComboBox { ItemsSource = Enum.GetValues<ItemKind>().Select(KindLabel).ToArray(), SelectedIndex = 0, Width = 125 };
        create.Children.Add(kind); create.Children.Add(ActionButton("添加", () => AddItemAsync((ItemKind)kind.SelectedIndex))); DockPanel.SetDock(create, Dock.Bottom); left.Children.Add(create);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(7) };
        actions.Children.Add(ActionButton("改名", RenameAsync)); actions.Children.Add(ActionButton("删除", DeleteAsync)); DockPanel.SetDock(actions, Dock.Bottom); left.Children.Add(actions); left.Children.Add(tree);
        center.Children.Add(new Border { Background = Brush.Parse("#101F30"), CornerRadius = new CornerRadius(6), Child = left });
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 1); center.Children.Add(splitter);
        var tabs = new TabControl { ItemsSource = new[] { new TabItem { Header = "ST 编程", Content = EditorPane() }, new TabItem { Header = "周期任务", Content = TasksPane() }, new TabItem { Header = "硬件组态", Content = HardwarePane() }, new TabItem { Header = "连接与工具", Content = SettingsPane() }, new TabItem { Header = "下载与激活", Content = DeploymentPane() }, new TabItem { Header = "在线监视", Content = OnlinePane() }, new TabItem { Header = "网络组态", Content = NetworkConfigurationPane() } } }; Register(tabs, "WorkbenchTabs"); Grid.SetColumn(tabs, 2); center.Children.Add(tabs);
        var librarySplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(librarySplitter, 3); center.Children.Add(librarySplitter);
        var library = InstructionLibraryPane(); Grid.SetColumn(library, 4); center.Children.Add(library); SetupInstructionDrop();
        var footer = new TabControl { Margin = new Thickness(14, 6, 14, 12), ItemsSource = new[] { new TabItem { Header = "问题 / 定位", Content = diagnostics }, new TabItem { Header = "编译与运行日志", Content = log } } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,5,210") };
        root.Children.Add(header); Grid.SetRow(commands, 1); root.Children.Add(commands); Grid.SetRow(center, 2); root.Children.Add(center);
        var rowSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(rowSplitter, 3); root.Children.Add(rowSplitter);
        Grid.SetRow(footer, 4); root.Children.Add(footer); Content = root;
        tree.SelectionChanged += (_, _) => { if (tree.SelectedItem is TreeViewItem { Tag: Guid id }) SelectItem(id); else if (tree.SelectedItem is TreeViewItem { Tag: PathResourceReference path }) SelectPath(path.Id, path.Version); else if (tree.SelectedItem is TreeViewItem { Tag: "path-resources" }) ShowPathWorkspace(); else if (tree.SelectedItem is TreeViewItem { Tag: ProjectResourceReference resource }) SelectItem(resource.ItemId); else if(tree.SelectedItem is TreeViewItem{Tag:EtherCatStation station}) { selectedStationId=station.Id;RefreshConfigurationViews();NavigateTo(2); } else if(tree.SelectedItem is TreeViewItem{Tag:"network-configuration"})NavigateTo(6); };
        editor.TextChanged += (_, _) => { if (!loading && selected is not null) { sources[selected.Id] = new(selected.Id, selected.SourcePath, editor.Text); MarkDirty(); RefreshResourceMembers(selected); } };
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateHint();
        editor.KeyDown += EditorKeyDown;
        KeyDown += (_, e) => { if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S) { e.Handled = true; _ = Run(SaveAsync); } else if (e.Key == Key.F7) { e.Handled = true; _ = Run(CompileAsync); } };
        diagnostics.DoubleTapped += (_, _) => { if (diagnostics.SelectedIndex >= 0 && diagnostics.SelectedIndex < currentDiagnostics.Count) GoTo(currentDiagnostics[diagnostics.SelectedIndex]); };
        // Target identity must change synchronously, before a new connection can
        // start; TextChanged is queued and can invalidate a later connection.
        sshHost.PropertyChanged += (_, e) => { if(e.Property==TextBox.TextProperty)ResetDeviceIdentity(); };
        sshUser.PropertyChanged += (_, e) => { if(e.Property==TextBox.TextProperty)ResetDeviceIdentity(); };
        endpoint.PropertyChanged += (_, e) => { if(e.Property==TextBox.TextProperty)ResetDeviceIdentity(); };
        Closing += OnClosing;
        Closed += (_, _) => { connectionGeneration++; buildCancellation?.Cancel(); monitorTimer.Stop(); if (debugClient is not null) _ = debugClient.DisposeAsync(); runtime?.Dispose(); privateTunnel?.Dispose(); privateTunnel = null; password.Text = ""; debugSessionToken = null; };
        monitorTimer.Tick += async (_, _) => await Run(PollVariablesAsync);
        instanceTree.SelectionChanged += (_, _) => { if (instanceTree.SelectedItem is TreeViewItem { Tag: string path }) { trendPath = path; RefreshTrend(); } };
        endpoint.Text = settings.Endpoint; username.Text = settings.Username; compilerPath.Text = settings.CompilerPath; compilerVersion.Text = settings.CompilerVersion; certificateFingerprint.Text = settings.CertificateFingerprint;
        SetNewProject();
        AppendLog("工程站已就绪。编译器为独立工具；本地检查成功不会自动下载或运行。", false);
    }
    private void Register(Control control, string name) { control.Name = name; scope.Register(name, control); }
    private static string KindLabel(ItemKind kind) => kind switch { ItemKind.Program => "程序", ItemKind.Function => "函数", ItemKind.FunctionBlock => "功能块", ItemKind.DataType => "数据类型", _ => "全局变量" };
    private Button ActionButton(string title, Func<Task> action) { var button = new Button { Content = title, Padding = new Thickness(12, 7) }; button.Click += async (_, _) => await Run(action); return button; }
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { AppendLog("操作已取消；设备状态需重新查询。", false); }
        catch (Exception e) { AppendLog(e.Message, true); }
    }
    private Control EditorPane()
    {
        var pane = new DockPanel { Background = Brush.Parse("#111B2A") };
        DockPanel.SetDock(documentLabel, Dock.Top); pane.Children.Add(documentLabel);
        var bar = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
        var start = ActionButton("开启 ST 在线监视", BindDebugAsync); Register(start, "StartInlineMonitor");
        bar.Children.Add(start); bar.Children.Add(ActionButton("断开监视", DisconnectDebugAsync)); bar.Children.Add(monitorInstance);
        DockPanel.SetDock(bar, Dock.Top); pane.Children.Add(bar); DockPanel.SetDock(inlineStatus, Dock.Top); pane.Children.Add(inlineStatus);
        DockPanel.SetDock(hint, Dock.Bottom); pane.Children.Add(hint); pane.Children.Add(editor); editorWorkspace = pane; return pane;
    }
    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 4), Foreground = Brush.Parse("#A9BAD1"), TextWrapping = TextWrapping.Wrap };
    private static Control ScrollPanel(StackPanel panel) => new ScrollViewer { Content = panel, Margin = new Thickness(18) };
    private Control TasksPane()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "周期任务", FontSize = 22, FontWeight = FontWeight.Bold });
        panel.Children.Add(Label("当前编译闭环仅启用一个周期任务。任务配置随工程保存；多任务运行需通过目标 Runtime 调度验证。"));
        panel.Children.Add(Label("任务名")); panel.Children.Add(taskName); panel.Children.Add(Label("周期（ms）")); panel.Children.Add(taskPeriod);
        panel.Children.Add(Label("绑定程序")); panel.Children.Add(taskProgram);
        panel.Children.Add(ActionButton("应用任务配置", () =>
        {
            if (!int.TryParse(taskPeriod.Text, out var ms) || ms is < 1 or > 60000) throw new InvalidDataException("任务周期必须为 1–60000 ms");
            if (!Regex.IsMatch(taskName.Text ?? "", @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new InvalidDataException("任务名必须是 ST 标识符");
            if (busy) return Task.CompletedTask;
            var program = project.Items.SingleOrDefault(i => i.Kind == ItemKind.Program && i.Name == (taskProgram.SelectedItem as string)) ?? throw new InvalidDataException("请选择绑定程序");
            var task = project.Tasks.Single(t => t.Enabled);
            if (project.Tasks.Any(t => !ReferenceEquals(t, task) && t.Name.Equals(taskName.Text, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("任务名称重复");
            task.Name = taskName.Text!; task.ProgramId = program.Id; task.IntervalMilliseconds = ms; MarkDirty(); RefreshTree(); return Task.CompletedTask;
        }));
        panel.Children.Add(Label("ST 编译生成本地 C++ 产物，Runtime 包生成完成前不宣称任务已在设备运行。")); panel.Children.Add(MotionCyclePane()); return ScrollPanel(panel);
    }
    private Control HardwarePane() => HardwareConfigurationPane();
    private Control SettingsPane()
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "连接与工具", FontSize = 22, FontWeight = FontWeight.Bold });
        panel.Children.Add(Label("设备 SSH 地址 / Linux 用户名"));
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sshHost, sshUser } });
        privateConnectButton = ActionButton("SSH 连接 Runtime（免密码）", ConnectPrivateRuntimeAsync);
        Register(privateConnectButton, "PrivateConnectButton"); panel.Children.Add(privateConnectButton);
        panel.Children.Add(Label("已配置设备：192.168.1.31，用户 a。此按钮自动建立隧道并登录，无需填写下面的 Runtime 密码。"));
        panel.Children.Add(Label("Runtime HTTPS 地址")); panel.Children.Add(endpoint); panel.Children.Add(Label("Runtime 用户名（与 SSH 账号可不同）")); panel.Children.Add(username); panel.Children.Add(password);
        connectButton = ActionButton("账号密码连接 Runtime（直接 HTTPS）", ConnectAsync); panel.Children.Add(connectButton);
        panel.Children.Add(Label("独立 ST 编译器路径")); panel.Children.Add(compilerPath); panel.Children.Add(ActionButton("选择编译器", PickCompilerAsync));
        panel.Children.Add(Label("锁定编译器版本输出")); panel.Children.Add(compilerVersion);
        panel.Children.Add(ActionButton("保存配置", () => { var next = new WorkbenchSettings(endpoint.Text ?? "", username.Text ?? "", compilerPath.Text ?? "", compilerVersion.Text ?? "", certificateFingerprint.Text ?? ""); next.Save(); settings = next; ResetDeviceIdentity(); runtimeDetails.Text = "连接配置已变更，设备状态不可用；请重新连接。"; AppendLog("连接和编译配置已保存；密码不保存。", false); return Task.CompletedTask; }));
        panel.Children.Add(Label("设备证书 SHA256（可选，需通过已验证 SSH 核对）")); panel.Children.Add(certificateFingerprint);
        panel.Children.Add(Label("证书固定只允许该设备自签证书，仍验证主机名。SSH 连接使用独立本机端口；HTTPS 地址只用于直接账号密码连接。")); return ScrollPanel(panel);
    }
    private Control OnlinePane()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "在线状态", FontSize = 22, FontWeight = FontWeight.Bold });
        refreshButton = ActionButton("读取设备状态 / 日志", RefreshRuntimeAsync); refreshButton.IsEnabled = false; panel.Children.Add(refreshButton); panel.Children.Add(runtimeDetails);
        panel.Children.Add(Label("变量、实例与趋势")); panel.Children.Add(monitorExplanation);
        panel.Children.Add(ActionButton("绑定已激活程序并开始只读监视", BindDebugAsync));
        panel.Children.Add(ActionButton("断开变量监视", DisconnectDebugAsync));
        var samples = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Height = 170 }; samples.Children.Add(instanceTree); Grid.SetColumn(trendView, 1); samples.Children.Add(trendView); panel.Children.Add(samples);
        panel.Children.Add(Label("重新打开工程后需生成对应候选包并绑定。变量与程序版本必须一致；选择树中的数值变量查看趋势。监视为只读，不改变变量或启停设备。")); return ScrollPanel(panel);
    }
    private Control DeploymentPane()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "下载与显式激活", FontSize = 22, FontWeight = FontWeight.Bold });
        panel.Children.Add(Label("SSH 目标在“连接与工具”页设置，使用已配置密钥及严格主机身份验证。"));
        panel.Children.Add(ActionButton("查询真实状态", () => DeploymentActionAsync("status")));
        panel.Children.Add(ActionButton("首次登记空设备基线", () => DeploymentActionAsync("adopt-empty-baseline")));
        panel.Children.Add(ActionButton("登记已有停止程序基线", () => DeploymentActionAsync("adopt-baseline")));
        panel.Children.Add(deploymentDetails);
        panel.Children.Add(Label("激活成功后保持停止；点击顶部“▶ 运行”执行已激活程序，点击“■ 停止”正常停止。按钮灰色时先点击“刷新设备状态”。"));
        panel.Children.Add(ActionButton("恢复中断的激活事务", () => DeploymentActionAsync("recover")));
        panel.Children.Add(Label("下载先暂存，再在 Linux 隔离编译，保持活动版本。激活会暂时中断在线连接，重启安全模式后保持停止；运行是另一个明确操作。通信中断后先查询实际状态，不自动重试。"));
        return ScrollPanel(panel);
    }
    private bool CandidateMatchesProject()
    {
        if (candidatePackage is null) return false;
        var current = ProjectValidator.Capture(project, GetSources());
        return current.SourceHash == candidateSourceHash && current.HardwareHash == candidateHardwareHash;
    }
    private void ResetDeviceIdentity() { motionRealtimeStatus.Text = "设备目标已改变，实时状态未核验。"; connectionGeneration++; runtime?.Dispose(); runtime = null; privateTunnel?.Dispose(); privateTunnel = null; privateRuntimeEndpoint = null; debugSessionToken = null; refreshButton.IsEnabled = false; monitorTimer.Stop(); monitorSession?.MarkUnavailable(DateTimeOffset.UtcNow); var priorDebug = debugClient; debugClient = null; if (priorDebug is not null) _ = priorDebug.DisposeAsync(); RefreshInstanceTree(); RefreshTrend(); remoteKnown = false; activeBuildHash = null; compiledBuildHash = null; activateButton.IsEnabled = false; startButton.IsEnabled = stopButton.IsEnabled = false; deviceRunStatus.Text = "设备状态未确认"; deploymentDetails.Text = "目标已改变，请重新查询状态和编译候选。"; RefreshNetworkTopology(); }
    private async Task<DeploymentResponse> SendDeploymentAsync(string action, string? packageBase64 = null, string? packageHash = null)
    {
        var request = new DeploymentRequest(action, Guid.NewGuid().ToString("N"), action is "stage" or "compile" or "activate" ? candidatePackage?.BuildHash : null, activeBuildHash, packageBase64, packageHash);
        var target = operationTarget ?? new(sshHost.Text ?? "", sshUser.Text ?? "");
        var response = await new RemoteDeploymentClient(knownHostsPath: Path.Combine(WorkbenchSettings.WorkspaceRoot, "artifacts", "ssh-known-hosts")).ExecuteAsync(target, request);
        if (target.Host != sshHost.Text || target.User != sshUser.Text) throw new InvalidOperationException("操作期间目标已改变，结果属于原设备；请查询当前目标");
        deploymentDetails.Text = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        ApplyDeploymentResponse(response, action);
        AppendLog($"设备操作 {action}：{response.State}", false);
        return response;
    }
    private void ApplyDeploymentResponse(DeploymentResponse response, string action)
    {
        if (!response.Success) { remoteKnown = false; startButton.IsEnabled = stopButton.IsEnabled = false; deviceRunStatus.Text = "设备状态未确认"; activateButton.IsEnabled = false; throw new InvalidOperationException(response.Message.Length > 0 ? response.Message : "设备操作结果未确认，请查询真实状态"); }
        if (response.Active is { ValueKind: JsonValueKind.Object } active) { activeBuildHash = active.TryGetProperty("build_hash", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null; remoteKnown = response.State is not ("unknown" or "unavailable" or "failed-or-unknown"); }
        else if (action == "status") { activeBuildHash = null; remoteKnown = response.Active is { ValueKind: JsonValueKind.Object }; }
        startButton.IsEnabled = remoteKnown && activeBuildHash is not null && response.State == "stopped";
        stopButton.IsEnabled = remoteKnown && activeBuildHash is not null && response.State == "running";
        deviceRunStatus.Text = response.State switch { "stopped" => "设备：已停止", "running" => "设备：运行中", "activated-not-running" => "已激活，待确认停止状态", _ => "设备状态需刷新" };
        if (remoteKnown && candidatePackage is not null && activeBuildHash == candidatePackage.BuildHash) buildStatus.Text = response.State == "running" ? "当前候选已激活 · 真实运行中" : response.State == "stopped" ? "当前候选已激活 · 已停止" : "当前候选为设备活动版本";
    }
    public Task RefreshDeploymentStatusAsync() => DeploymentActionAsync("status");
    private async Task DeploymentActionAsync(string action)
    {
        if (deploymentBusy) return;
        if (action is "start" or "stop" && !remoteKnown) throw new InvalidOperationException("请先查询当前活动程序身份");
        deploymentBusy = true; operationTarget = new(sshHost.Text ?? "", sshUser.Text ?? "");
        try { await SendDeploymentAsync(action); if (action is "activate" or "recover" or "start" or "stop") { runtimeDetails.Text = "设备控制状态已变化，请重新读取状态。"; await DisconnectDebugAsync(); } if (action is "activate" or "recover") await SendDeploymentAsync("status"); }
        catch { remoteKnown = false; startButton.IsEnabled = stopButton.IsEnabled = false; deviceRunStatus.Text = "设备状态未确认，请刷新"; throw; }
        finally { deploymentBusy = false; operationTarget = null; }
    }
    private async Task StageAndCompileAsync()
    {
        if (deploymentBusy || busy) return;
        if (!CandidateMatchesProject()) throw new InvalidOperationException("工程已变更，请重新生成候选包");
        deploymentBusy = true; busy = true; operationTarget = new(sshHost.Text ?? "", sshUser.Text ?? ""); downloadButton.IsEnabled = false; activateButton.IsEnabled = false; compiledBuildHash = null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(candidatePackage!.PackagePath);
            if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("程序包超过上传限制");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            buildStatus.Text = "设备暂存中…"; await SendDeploymentAsync("stage", Convert.ToBase64String(bytes), hash);
            buildStatus.Text = "Linux 隔离编译中…"; var result = await SendDeploymentAsync("compile");
            ApplyCompiledCandidate(result);
        }
        finally { deploymentBusy = false; busy = false; operationTarget = null; downloadButton.IsEnabled = CandidateMatchesProject(); }
    }
    private void ApplyCompiledCandidate(DeploymentResponse result)
    {
        if (result.State != "compiled" || candidatePackage is null) throw new InvalidOperationException("Linux 编译结果未确认");
        compiledBuildHash = candidatePackage.BuildHash;
        var axisRequired = AxisControlBinding.UsesAxisControl(GetSources());
        var offlineAxisProperties = project.Hardware.Axes.Any(a => a.Mechanics is not null);
        motionDeploymentReady = !offlineAxisProperties && (project.Paths.Count == 0 || result.MotionReady == true) && (project.MotionCycle?.Enabled != true || result.MotionCycleReady == true) && (!axisRequired || result.AxisReady == true);
        activateButton.IsEnabled = CandidateMatchesProject() && motionDeploymentReady;
        if (!motionDeploymentReady) AppendLog("运动候选暂不能激活：" + (offlineAxisProperties ? "轴机械与回零参数尚未应用，控制核心缺少参数应用/确认接口。" : project.MotionCycle?.Enabled == true && result.MotionCycleReady != true ? result.MotionCycleMessage : axisRequired && result.AxisReady != true ? result.AxisMessage : result.MotionMessage), true);
        buildStatus.Text = motionDeploymentReady ? "Linux 编译通过 · 未激活" : offlineAxisProperties ? "Linux 编译通过 · 轴机械与回零参数尚未应用，不可激活" : project.MotionCycle?.Enabled == true && result.MotionCycleReady != true ? "Linux 编译通过 · 轴控周期执行端未就绪，不可激活" : axisRequired && result.AxisReady != true ? "Linux 编译通过 · 基础轴控后端未就绪，不可激活" : "Linux 编译通过 · 连续运动后端未就绪，不可激活";
    }
    private async Task ActivateAsync()
    {
        if (deploymentBusy || busy) return;
        if (project.Hardware.Axes.Any(a => a.Mechanics is not null)) throw new InvalidOperationException("轴机械与回零参数尚未应用，当前候选不可激活。");
        if (!CandidateMatchesProject() || compiledBuildHash != candidatePackage!.BuildHash) throw new InvalidOperationException("需要当前工程的 Linux 编译通过候选");
        if ((project.Paths.Count > 0 || project.MotionCycle?.Enabled == true || AxisControlBinding.UsesAxisControl(GetSources())) && !motionDeploymentReady) throw new InvalidOperationException("运动执行端未验证就绪，当前候选不可激活。");
        await DeploymentActionAsync("status");
        if (!remoteKnown) throw new InvalidOperationException("请先明确登记设备基线");
        if (!await ConfirmAsync("显式激活候选", "激活会短暂断开在线连接，并替换已登记的活动程序。成功后保持停止，需要另行点击运行。", "激活并保持停止")) return;
        if (!CandidateMatchesProject()) throw new InvalidOperationException("确认期间工程已变更，请重新编译");
        await DeploymentActionAsync("activate");
        buildStatus.Text = startButton.IsEnabled ? "已激活 · 已停止，请点击顶部 ▶ 运行" : "已激活 · 运行状态需刷新";
    }
    private IReadOnlyList<SourceDocument> GetSources() => project.Items.Select(i => sources[i.Id]).ToArray();
    private void SetNewProject()
    {
        var main = new ProjectItem(); project = new StProject { Name = "ST 示例工程", Items = [main], Tasks = [new PlcTask { ProgramId = main.Id }] };
        sources = new() { [main.Id] = new(main.Id, main.SourcePath, "PROGRAM Main\nVAR\n    Count : DINT := 0;\n    Enabled : BOOL := TRUE;\nEND_VAR\n\nIF Enabled THEN\n    Count := Count + 1;\nEND_IF;\nEND_PROGRAM\n") };
        projectPath = null; selected = null; dirty = true; editGeneration++; taskName.Text = "Cyclic"; taskPeriod.Text = "10"; RefreshPaths(); RefreshTree(); SelectItem(main.Id); RefreshHardware(); RestoreNetworkConfiguration(); ResetBuild(); UpdateStatus();
    }
    private void RefreshTree()
    {
        var groups = new List<TreeViewItem>();
        foreach (var kind in Enum.GetValues<ItemKind>()) groups.Add(new TreeViewItem { Header = KindLabel(kind), IsExpanded = true, ContextMenu = CategoryMenu(kind), ItemsSource = project.Items.Where(i => i.Kind == kind).Select(ResourceNode).ToArray() });
        groups.Add(PathResourceGroupNode());
        groups.Add(new TreeViewItem { Header = "任务", IsExpanded = true, ContextMenu = TaskGroupMenu(), ItemsSource = project.Tasks.Select(TaskNode).ToArray() });
        groups.Add(new TreeViewItem { Header = "硬件", IsExpanded = true, ContextMenu = NavigationMenu("硬件组态", 2), ItemsSource = new[] { new TreeViewItem { Header = $"工程从站 ({Stations.Count})", IsExpanded=true, ContextMenu=NavigationMenu("硬件组态 / 从站",2),ItemsSource=Stations.Select((s,i)=>new TreeViewItem{Header=$"{i+1}. {s.Name}",Tag=s,ContextMenu=NavigationMenu("从站属性",2)}).ToArray() }, new TreeViewItem { Header = $"IO 映射 ({project.Hardware.Io.Count})", ContextMenu = NavigationMenu("IO 属性 / 映射", 2) }, new TreeViewItem { Header = $"轴资源 ({project.Hardware.Axes.Count})", ContextMenu = NavigationMenu("轴属性 / 配置", 2) } } });
        groups.Add(new TreeViewItem{Header="网络",Tag="network-configuration",IsExpanded=true,ContextMenu=NavigationMenu("网络组态",6),ItemsSource=new[]{new TreeViewItem{Header=project.Network?.ManagementHost??"管理连接未配置",Tag="network-configuration"},new TreeViewItem{Header=string.IsNullOrWhiteSpace(project.Network?.EtherCatInterface)?"EtherCAT 网卡未配置":project.Network.EtherCatInterface,Tag="network-configuration"}}});tree.ItemsSource = groups;
        taskProgram.ItemsSource = project.Items.Where(i => i.Kind == ItemKind.Program).Select(i => i.Name).ToArray();
        taskProgram.SelectedItem = project.Items.FirstOrDefault(i => i.Id == project.Tasks.Single(t => t.Enabled).ProgramId)?.Name;
        RefreshInstructionLibrary();
    }
    private void SelectItem(Guid id)
    {
        ShowEditorWorkspace();
        if (selected?.Id == id && sources.TryGetValue(id, out var current) && editor.Text == current.Text)
        {
            selected = project.Items.Single(i => i.Id == id);
            documentLabel.Text = $"{selected.Name}.st    ·    {KindLabel(selected.Kind)}";
            return;
        }
        selected = project.Items.Single(i => i.Id == id); loading = true; editor.Text = sources[id].Text; loading = false; documentLabel.Text = $"{selected.Name}.st    ·    {KindLabel(selected.Kind)}"; UpdateHint(); RefreshInlineMonitor();
    }
    private void ShowEditorWorkspace()
    {
        if (editorWorkspace is not null && this.FindControl<TabControl>("WorkbenchTabs")?.ItemsSource is IEnumerable<TabItem> items) items.First().Content = editorWorkspace;
    }
    private void ShowPathWorkspace()
    {
        if (this.FindControl<TabControl>("WorkbenchTabs") is not { } tabs || tabs.ItemsSource is not IEnumerable<TabItem> items) return;
        items.First().Content = PathPane(); tabs.SelectedIndex = 0;
    }
    private void RefreshHardware() { deviceList.ItemsSource = project.Hardware.Devices.Select(d => $"{d.Name} · 厂商 0x{d.VendorId:X8} / 产品 0x{d.ProductCode:X8} / 修订 0x{d.Revision:X8}").ToArray(); pdoList.ItemsSource = Array.Empty<string>(); ioList.ItemsSource = project.Hardware.Io.Select(i => $"{i.Name}  →  {i.Address}  ·  {i.Type}  ·  {(i.IsInput ? "输入" : "输出")}").ToArray(); axisList.ItemsSource = project.Hardware.Axes.Select(a => $"{a.Name}  →  {a.ControllerAxis}  ·  [{a.MinPosition}, {a.MaxPosition}] {a.Unit}").ToArray(); RefreshMotionCycle(); RefreshConfigurationViews(); RefreshTree(); }
    private async Task ImportEsiAsync()
    {
        if (busy) return;
        var generation = editGeneration;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入 EtherCAT ESI", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("ESI XML") { Patterns = ["*.xml"] }] });
        if (files.Count == 0 || busy || generation != editGeneration) return;
        await using var stream = await files[0].OpenReadAsync();
        var devices = EsiImporter.Read(stream);
        if (generation != editGeneration) throw new InvalidOperationException("导入期间工程已变更，请重新导入");
        project.Hardware.Devices.AddRange(devices); MarkDirty(); RefreshHardware(); RefreshTree();
        AppendLog($"已导入 {devices.Count} 个 ESI 设备描述。实际从站地址与 IO 映射需明确配置。", false);
    }
    private void MarkDirty() { editGeneration++; dirty = true; monitorTimer.Stop(); monitorSession?.MarkUnavailable(DateTimeOffset.UtcNow); if (debugClient is not null) { var priorDebug = debugClient; debugClient = null; _ = priorDebug.DisposeAsync(); monitorExplanation.Text = "工程已变更，监视身份过期；请重新编译并绑定。"; RefreshInstanceTree(); RefreshTrend(); } RefreshInlineMonitor(); UpdateStatus(); downloadButton.IsEnabled = false; activateButton.IsEnabled = false; buildStatus.Text = "源码或配置已变更 · 需重新编译"; }
    private void ResetBuild() { motionRealtimeStatus.Text = "设备实时状态未核验。"; candidatePackage = null; candidateSourceHash = null; compiledBuildHash = null; downloadButton.IsEnabled = false; activateButton.IsEnabled = false; currentDiagnostics = []; diagnosticsSourceHash = null; diagnostics.ItemsSource = Array.Empty<string>(); buildStatus.Text = "尚未编译"; RefreshInlineMonitor(); }
    private void UpdateStatus() { projectStatus.Text = $"{project.Name}  ·  {(dirty ? "未保存" : "已保存")}  ·  {projectPath ?? "新工程"}"; Title = $"{(dirty ? "* " : "")}{project.Name} · ST Workbench"; }
    private void AppendLog(string message, bool error)
    {
        var text = log.Text ?? ""; if (text.Length > 150000) text = text[^100000..];
        log.Text = text + $"[{DateTimeOffset.Now:HH:mm:ss}] {(error ? "错误 · " : "")}{message}\n";
        if (error) { buildStatus.Text = "操作未完成 · 查看日志"; }
    }
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!dirty) return true;
        return await ConfirmAsync("未保存的修改", "当前工程有未保存修改。是否放弃修改？", "放弃修改");
    }
    private async Task<bool> ConfirmAsync(string title, string message, string accept)
    {
        var window = new Window { Title = title, Width = 460, Height = 200, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var yes = new Button { Content = accept }; var no = new Button { Content = "取消" };
        yes.Click += (_, _) => window.Close(true); no.Click += (_, _) => window.Close(false);
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 20, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { yes, no } } } };
        return await window.ShowDialog<bool>(this);
    }
    private bool allowClose;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose || !dirty) return; e.Cancel = true; if (await ConfirmDiscardAsync()) { allowClose = true; Close(); }
    }
    private async Task NewProjectAsync() { if (busy || !await ConfirmDiscardAsync()) return; SetNewProject(); }
    private async Task OpenAsync()
    {
        if (busy || !await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "打开 ST 工程", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("ST 工程") { Patterns = ["*.stproj"] }] });
        if (files.Count == 0) return; var path = files[0].TryGetLocalPath() ?? throw new InvalidDataException("只支持本地工程文件");
        await LoadAtAsync(path);
    }
    public async Task LoadAtAsync(string path)
    {
        if (busy) throw new InvalidOperationException("另一个工程操作尚未结束");
        var generation = editGeneration; busy = true;
        try
        {
            var loaded = await store.LoadAsync(path);
            if (generation != editGeneration) throw new InvalidOperationException("读取期间工程已修改，已保留修改；请重新打开");
            project = loaded.Project; sources = loaded.Sources.ToDictionary(s => s.ItemId); projectPath = path; dirty = false; editGeneration++;
            taskName.Text = project.Tasks.Single(t => t.Enabled).Name; taskPeriod.Text = project.Tasks.Single(t => t.Enabled).IntervalMilliseconds.ToString(); RefreshPaths(); RefreshTree(); SelectItem(project.Items[0].Id); RefreshHardware(); RestoreNetworkConfiguration(); ResetBuild(); UpdateStatus();
        }
        finally { busy = false; }
    }
    private async Task SaveAsync() { if (busy) return; if (projectPath is null) { await SaveAsAsync(); return; } await SaveAtAsync(projectPath); }
    private async Task SaveAsAsync()
    {
        if (busy) return; var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "保存 ST 工程", SuggestedFileName = "Project.stproj", DefaultExtension = "stproj", FileTypeChoices = [new FilePickerFileType("ST 工程") { Patterns = ["*.stproj"] }] });
        if (file is null) return; var path = file.TryGetLocalPath() ?? throw new InvalidDataException("只支持本地文件"); await SaveAtAsync(path);
    }
    public async Task SaveAtAsync(string path)
    {
        if (busy) return;
        var captured = ProjectValidator.Capture(project, GetSources());
        busy = true;
        try
        {
            await store.SaveAsync(path, project, GetSources());
            projectPath = path;
            var current = ProjectValidator.Capture(project, GetSources());
            dirty = current.SourceHash != captured.SourceHash || current.HardwareHash != captured.HardwareHash;
            UpdateStatus(); AppendLog(dirty ? "源版本已保存，保存期间的新修改仍未保存。" : "工程及源版本已保存。", false);
        }
        finally { busy = false; }
    }
    private async Task<string?> AskNameAsync(string title, string current)
    {
        var window = new Window { Title = title, Width = 430, Height = 210, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var box = new TextBox { Text = current }; var save = new Button { Content = "确定" }; var cancel = new Button { Content = "取消" };
        save.Click += (_, _) => window.Close(box.Text); cancel.Click += (_, _) => window.Close();
        window.KeyDown += (_, e) => { if (e.Key == Key.Enter) { window.Close(box.Text); e.Handled = true; } else if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { Label("使用英文字母、数字和下划线，首字符不能为数字。"), box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } } } }; return await window.ShowDialog<string?>(this);
    }
    private static string SourceTemplate(ItemKind kind, string name) => kind switch
    {
        ItemKind.Program => $"PROGRAM {name}\nVAR\nEND_VAR\n\nEND_PROGRAM\n",
        ItemKind.Function => $"FUNCTION {name} : DINT\nVAR_INPUT\n    Value : DINT;\nEND_VAR\n{name} := Value;\nEND_FUNCTION\n",
        ItemKind.FunctionBlock => $"FUNCTION_BLOCK {name}\nVAR_INPUT\n    Execute : BOOL;\nEND_VAR\nVAR_OUTPUT\n    Done : BOOL;\nEND_VAR\nDone := Execute;\nEND_FUNCTION_BLOCK\n",
        ItemKind.DataType => $"TYPE {name} : STRUCT\n    Value : DINT;\nEND_STRUCT;\nEND_TYPE\n",
        _ => $"VAR_GLOBAL\n    {name}_Value : DINT;\nEND_VAR\n"
    };
    private async Task AddItemAsync(ItemKind kind)
    {
        if (busy) return; var targetProject = project; var name = await AskNameAsync("添加" + KindLabel(kind), AvailableResourceName("New" + kind)); if (name is null || busy) return;
        if (!ReferenceEquals(targetProject, project)) throw new InvalidOperationException("工程已切换，请重新添加资源。");
        ValidateName(name); var item = new ProjectItem { Name = name, Kind = kind, SourcePath = $"sources/{name}.st" }; project.Items.Add(item); sources.Add(item.Id, new(item.Id, item.SourcePath, SourceTemplate(kind, name))); MarkDirty(); RefreshTree(); SelectItem(item.Id);
    }
    private void ValidateName(string name) { if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$") || project.Items.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("名称必须是有效且不重名的 ST 标识符"); }
    private string AvailableResourceName(string basis) { var name = basis; int number = 1; while (project.Items.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = basis + number++; return name; }
    private async Task RenameAsync()
    {
        if (busy || selected is null) return; var item = selected; var name = await AskNameAsync("修改对象名", item.Name); if (name is null || name == item.Name || busy) return;
        if (!project.Items.Contains(item)) throw new InvalidOperationException("工程已切换，请重新选择资源。"); ValidateName(name);
        var text = sources[item.Id].Text; if (item.Kind != ItemKind.GlobalVariables) text = language.RenameLocalIdentifier(text, item.Name, name);
        item.Name = name; item.SourcePath = $"sources/{name}.st"; sources[item.Id] = new(item.Id, item.SourcePath, text); MarkDirty(); RefreshTree(); SelectItem(item.Id); AppendLog("对象已改名。请编译检查其他源文件中的引用。", false);
    }
    private async Task DeleteAsync()
    {
        if (busy || selected is null) return; var item = selected; if (project.Tasks.Any(t => t.ProgramId == item.Id)) throw new InvalidDataException("该程序被周期任务引用，不能删除");
        if (!await ConfirmAsync("删除源对象", $"删除 {item.Name}？", "删除") || busy) return;
        if (!project.Items.Contains(item) || project.Tasks.Any(t => t.ProgramId == item.Id)) throw new InvalidOperationException("资源或任务引用已改变，请重新检查后删除。");
        project.Items.Remove(item); sources.Remove(item.Id); MarkDirty(); RefreshTree(); SelectItem(project.Items[0].Id);
    }
    private async Task PickCompilerAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择独立 ST 编译器", AllowMultiple = false }); if (files.Count > 0) compilerPath.Text = files[0].TryGetLocalPath();
    }
    private async Task CompileAsync()
    {
        if (busy) return; var docs = GetSources(); var errors = ProjectValidator.Validate(project, docs); ShowDiagnostics(errors); if (errors.Count > 0) { buildStatus.Text = "工程检查失败"; return; }
        var snapshot = ProjectValidator.Capture(project, docs); busy = true; buildButton.IsEnabled = false; buildCancellation = new CancellationTokenSource(); buildStatus.Text = "独立编译器检查中…";
        try
        {
            var compiler = new ExternalCompiler(expectedCompilerVersion: compilerVersion.Text);
            var result = snapshot.Paths.Count == 0 && !AxisControlBinding.UsesAxisControl(snapshot.Sources)
                ? await compiler.CompileAsync(snapshot, compilerPath.Text ?? "", Path.Combine(WorkbenchSettings.WorkspaceRoot, "artifacts", "builds"), buildCancellation.Token)
                : (await BuildPathPackageAsync(snapshot, buildCancellation.Token)).Compilation;
            ShowDiagnostics(result.Diagnostics, result.SourceHash); AppendLog(result.Log, !result.Success);
            var stale = ProjectValidator.Capture(project, GetSources()).SourceHash != result.SourceHash || ProjectValidator.Capture(project, GetSources()).HardwareHash != snapshot.HardwareHash;
            buildStatus.Text = result.Success ? stale ? "编译成功 · 源码已变更" : "ST 编译成功 · 尚未生成设备包 / 未激活" : "ST 编译失败";
            if (result.Success) AppendLog($"产物：{result.OutputDirectory}\n编译器：{result.CompilerVersion}\n构建身份：{result.SourceHash}", false);
        }
        finally { busy = false; buildButton.IsEnabled = true; buildCancellation.Dispose(); buildCancellation = null; }
    }
    public async Task GeneratePackageAsync()
    {
        if (busy) return;
        var snapshot = ProjectValidator.Capture(project, GetSources());
        var names = project.Items.Where(i => i.Kind == ItemKind.Program).ToDictionary(i => i.Id, i => i.Name);
        var node = Environment.GetEnvironmentVariable("ST_NODE_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        var adapter = Path.Combine(WorkbenchSettings.WorkspaceRoot, "tools", "compiler-adapter", "compile.mjs");
        if (!File.Exists(node) || !File.Exists(adapter)) throw new InvalidDataException("需要 Node.js 和独立编译适配工具，请参阅使用说明");
        busy = true; packageButton.IsEnabled = false; buildButton.IsEnabled = false; buildCancellation = new(); buildStatus.Text = "生成候选程序包…";
        try
        {
            var result = await new PackageBuilder(Path.Combine(WorkbenchSettings.WorkspaceRoot, "tools", "runtime-compat")).BuildAsync(snapshot, names, node, adapter, Path.GetDirectoryName(compilerPath.Text)!, Path.Combine(WorkbenchSettings.WorkspaceRoot, "artifacts", "packages-build"), buildCancellation.Token);
            ShowDiagnostics(result.Compilation.Diagnostics, snapshot.SourceHash); AppendLog(result.Compilation.Log, !result.Success);
            var current = ProjectValidator.Capture(project, GetSources());
            var stale = current.SourceHash != snapshot.SourceHash || current.HardwareHash != snapshot.HardwareHash;
            candidatePackage = result.Success && !stale ? result : null;
            candidateSourceHash = snapshot.SourceHash; candidateHardwareHash = snapshot.HardwareHash; compiledBuildHash = null;
            downloadButton.IsEnabled = candidatePackage is not null;
            buildStatus.Text = result.Success ? stale ? "候选包已生成 · 工程已修改" : "候选包已生成 · Linux 编译未确认 / 未激活" : "程序包生成失败";
            if (result.Success) AppendLog($"候选程序包：{result.PackagePath}\n构建身份：{result.BuildHash}\n调试符号身份：{result.SymbolTableHash}\n仅用于隔离验证；Linux 编译和闭源商用发行审查尚未通过。", false);
        }
        finally { busy = false; packageButton.IsEnabled = true; buildButton.IsEnabled = true; buildCancellation.Dispose(); buildCancellation = null; }
    }
    private Task<PackageResult> BuildPathPackageAsync(BuildSnapshot snapshot, CancellationToken token)
    {
        var names = project.Items.Where(i => i.Kind == ItemKind.Program).ToDictionary(i => i.Id, i => i.Name);
        var node = Environment.GetEnvironmentVariable("ST_NODE_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        var adapter = Path.Combine(WorkbenchSettings.WorkspaceRoot, "tools", "compiler-adapter", "compile.mjs");
        if (!File.Exists(node) || !File.Exists(adapter)) throw new InvalidDataException("需要 Node.js 和独立 ST 编译工具。");
        return new PackageBuilder(Path.Combine(WorkbenchSettings.WorkspaceRoot, "tools", "runtime-compat")).BuildAsync(snapshot, names, node, adapter, Path.GetDirectoryName(compilerPath.Text)!, Path.Combine(WorkbenchSettings.WorkspaceRoot, "artifacts", "path-checks"), token);
    }
    private void ShowDiagnostics(IReadOnlyList<Diagnostic> values, string? hash = null) { currentDiagnostics = values; diagnosticsSourceHash = hash; diagnostics.ItemsSource = values.Select(d => $"{d.Severity.ToUpperInvariant()}   {d.File}:{d.Line}:{d.Column}   {d.Message}").ToArray(); }
    private void GoTo(Diagnostic diagnostic, bool checkBuildIdentity = true)
    {
        if (checkBuildIdentity && diagnosticsSourceHash is not null && ProjectValidator.Capture(project, GetSources()).SourceHash != diagnosticsSourceHash) { AppendLog("诊断来自旧源版本，请重新编译后定位。", false); return; }
        var item = project.Items.FirstOrDefault(i => i.SourcePath.Equals(diagnostic.File, StringComparison.OrdinalIgnoreCase)); if (item is null) return; SelectItem(item.Id);
        int line = Math.Clamp(diagnostic.Line, 1, editor.Document.LineCount); var documentLine = editor.Document.GetLineByNumber(line); int column = Math.Clamp(diagnostic.Column, 1, documentLine.Length + 1);
        editor.TextArea.Caret.Offset = documentLine.Offset + column - 1; editor.ScrollTo(line, column); editor.Focus();
    }
    private void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true; var offset = editor.CaretOffset; var before = editor.Text[..offset]; var prefix = Regex.Match(before, @"[A-Za-z_][A-Za-z0-9_]*$").Value;
            var window = new CompletionWindow(editor.TextArea) { StartOffset = offset - prefix.Length }; foreach (var symbol in language.Complete(language.Index(GetSources()), prefix)) window.CompletionList.CompletionData.Add(new SymbolCompletion(symbol));
            if (window.CompletionList.CompletionData.Count > 0) window.Show();
        }
        else if (e.Key == Key.F12)
        {
            e.Handled = true; var text = editor.Text; int start = editor.CaretOffset, end = start; while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--; while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
            var name = text[start..end]; var symbol = language.Index(GetSources()).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); if (symbol is not null) GoTo(new(symbol.File, symbol.Line, symbol.Column, "", ""), false);
        }
    }
    private void UpdateHint()
    {
        if (selected is null) return; var before = editor.Text[..Math.Clamp(editor.CaretOffset, 0, editor.Text.Length)]; var match = Regex.Match(before, @"([A-Za-z_][A-Za-z0-9_]*)\s*\([^()]*$");
        hint.Text = match.Success ? language.Signature(language.Index(GetSources()), match.Groups[1].Value) ?? "Ctrl+Space 补全 · F12 定义" : "Ctrl+Space 补全 · F12 定义 · Ctrl+S 保存 · F7 编译检查";
    }
    private async Task ConnectAsync()
    {
        if (connecting) return;
        connecting = true; connectButton.IsEnabled = privateConnectButton.IsEnabled = false; var generation = ++connectionGeneration;
        privateTunnel?.Dispose(); privateTunnel = null; privateRuntimeEndpoint = null;
        runtime?.Dispose(); runtime = null; debugSessionToken = null; await DisconnectDebugAsync(); refreshButton.IsEnabled = false; runtimeDetails.Text = "连接中，设备状态尚不可用。";
        try { var next = new OpenPlcClient(new Uri(endpoint.Text ?? ""), certificateFingerprint: certificateFingerprint.Text); try { await next.LoginAsync(username.Text ?? "", password.Text ?? ""); } catch { next.Dispose(); throw; } if (generation != connectionGeneration) { next.Dispose(); return; } runtime = next; refreshButton.IsEnabled = true; AppendLog("Runtime 认证成功，正在读取实际状态。", false); await RefreshRuntimeAsync(); }
        catch { if (generation == connectionGeneration) runtimeDetails.Text = "连接失败，设备状态不可用。"; throw; }
        finally { password.Text = ""; connecting = false; connectButton.IsEnabled = privateConnectButton.IsEnabled = true; RefreshNetworkTopology(); }
    }
    public async Task ConnectPrivateRuntimeAsync()
    {
        if (connecting) return;
        connecting = true; connectButton.IsEnabled = privateConnectButton.IsEnabled = false; var generation = ++connectionGeneration;
        var host = sshHost.Text ?? ""; var user = sshUser.Text ?? ""; var pin = certificateFingerprint.Text ?? "";
        var knownHosts = Path.Combine(WorkbenchSettings.WorkspaceRoot, "artifacts", "ssh-known-hosts");
        privateTunnel?.Dispose(); privateTunnel = null; privateRuntimeEndpoint = null;
        runtime?.Dispose(); runtime = null; debugSessionToken = null; refreshButton.IsEnabled = false; runtimeDetails.Text = "正在通过 SSH 私有会话认证…";
        SshRuntimeTunnel? tunnel = null;
        try
        {
            await DisconnectDebugAsync();
            tunnel = await SshRuntimeTunnel.ConnectAsync(host, user, knownHosts, pin);
            if (generation != connectionGeneration) return;
            var token = await new SshSessionBroker().AuthenticateAsync(host, user, knownHosts);
            if (generation != connectionGeneration) return;
            var next = new OpenPlcClient(tunnel.Endpoint, certificateFingerprint: pin); next.UseSessionToken(token);
            privateTunnel = tunnel; privateRuntimeEndpoint = tunnel.Endpoint; tunnel = null;
            runtime = next; debugSessionToken = token; await RefreshRuntimeAsync(); await RefreshDeploymentStatusAsync();
            if (generation == connectionGeneration) AppendLog($"SSH 隧道与 Runtime 登录成功：{host}，已读取设备实际状态，无需输入密码。", false);
        }
        catch
        {
            if (generation == connectionGeneration)
            {
                runtime?.Dispose(); runtime = null; debugSessionToken = null; privateTunnel?.Dispose(); privateTunnel = null; privateRuntimeEndpoint = null;
                refreshButton.IsEnabled = false; runtimeDetails.Text = "SSH 连接未确认，设备状态不可用；请检查设备网络、密钥、服务和证书。";
            }
            throw;
        }
        finally { tunnel?.Dispose(); password.Text = ""; connecting = false; connectButton.IsEnabled = privateConnectButton.IsEnabled = true; RefreshNetworkTopology(); }
    }
    private async Task DisconnectDebugAsync()
    {
        monitorTimer.Stop(); monitorSession?.MarkUnavailable(DateTimeOffset.UtcNow);
        var prior = debugClient; debugClient = null;
        if (prior is not null) await prior.DisposeAsync();
        monitorExplanation.Text = "变量监视已断开；点击“绑定已激活程序并开始只读监视”重新连接。需要当前工程的候选包与设备活动程序一致。"; RefreshInstanceTree(); RefreshTrend();
    }
    public async Task BindDebugAsync()
    {
        if (bindingDebug) return;
        bindingDebug = true;
        try { await BindDebugCoreAsync(); }
        catch (Exception error) { monitorExplanation.Text = "变量监视未连接：" + error.Message; inlineStatus.Text = monitorExplanation.Text; throw; }
        finally { bindingDebug = false; }
    }
    private async Task BindDebugCoreAsync()
    {
        await DisconnectDebugAsync();
        if (debugSessionToken is null) throw new InvalidOperationException("请先通过 SSH 密钥连接私有 Runtime");
        if (!CandidateMatchesProject()) throw new InvalidOperationException("需要当前工程的候选包和对应调试符号");
        if (deploymentBusy) throw new InvalidOperationException("等待设备操作结束后再绑定监视");
        deploymentBusy = true; operationTarget = new(sshHost.Text ?? "", sshUser.Text ?? "");
        try
        {
            var state = await SendDeploymentAsync("status");
            if (state.Active is not { ValueKind: JsonValueKind.Object } active || active.GetProperty("build_hash").GetString() != candidatePackage!.BuildHash) throw new InvalidOperationException("设备活动版本与本工程候选不一致");
            using var zip = System.IO.Compression.ZipFile.OpenRead(candidatePackage.PackagePath);
            var entry = zip.GetEntry("debug-map.json") ?? throw new InvalidDataException("缺少权威调试映射");
            if (entry.Length > 1024 * 1024) throw new InvalidDataException("调试映射超过容量");
            using var content = entry.Open(); using var memory = new MemoryStream(); await content.CopyToAsync(memory);
            var mapHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(memory.ToArray()));
            if (!mapHash.Equals(active.GetProperty("symbol_table_hash").GetString(), StringComparison.OrdinalIgnoreCase) || !mapHash.Equals(candidatePackage.SymbolTableHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("调试映射身份不一致");
            using var map = JsonDocument.Parse(memory.ToArray());
            var expectedMd5 = map.RootElement.GetProperty("md5").GetString();
            if (expectedMd5 != active.GetProperty("program_md5").GetString()) throw new InvalidDataException("程序 MD5 与活动身份不一致");
            var symbols = map.RootElement.GetProperty("leaves").EnumerateArray().Select(leaf => new DebugSymbol(leaf.GetProperty("path").GetString()!, leaf.GetProperty("type").GetString()!, new(leaf.GetProperty("arrayIdx").GetByte(), leaf.GetProperty("elemIdx").GetUInt16(), leaf.GetProperty("size").GetUInt16()))).ToArray();
            if (symbols.Length is < 1 or > 256 || symbols.Sum(s => (int)s.Address.ByteSize) > 4086 || symbols.Any(s => string.IsNullOrWhiteSpace(s.Path) || string.IsNullOrWhiteSpace(s.Type)) || symbols.Select(s => s.Path).Distinct().Count() != symbols.Length) throw new InvalidDataException("调试映射容量或路径无效");
            var client = new DebugSocketClient(privateRuntimeEndpoint ?? new Uri(endpoint.Text ?? ""), certificateFingerprint.Text);
            try
            {
                await client.ConnectAsync(debugSessionToken); var identity = await client.GetProgramMd5Async();
                if (identity.Md5 != expectedMd5) throw new InvalidDataException("实际运行程序与调试映射不一致");
                var counts = await client.GetVariableCountsAsync();
                if (symbols.Any(s => s.Address.ArrayIndex >= counts.Count || s.Address.ElementIndex >= counts[s.Address.ArrayIndex])) throw new InvalidDataException("设备变量布局与符号映射不一致");
                if (!CandidateMatchesProject()) throw new InvalidOperationException("绑定期间工程已变更");
                debugClient = client; debugSymbols = symbols; debugLittleEndian = identity.LittleEndian; activeProgramMd5 = expectedMd5;
                var session = new MonitorIdentity(candidateSourceHash!, mapHash, Guid.NewGuid().ToString("N"));
                monitorSession = new(session, session, TimeSpan.FromSeconds(2)); monitorSequence = 0; trendPath = symbols[0].Path;
                await PollVariablesAsync(); monitorTimer.Start();
            }
            catch { await client.DisposeAsync(); throw; }
        }
        finally { deploymentBusy = false; operationTarget = null; }
    }
    public async Task PollVariablesAsync()
    {
        if (polling) { RefreshInstanceTree(); RefreshTrend(); return; }
        if (debugClient is null || monitorSession is null) return;
        if (!CandidateMatchesProject()) { await DisconnectDebugAsync(); return; }
        polling = true; var client = debugClient; var session = monitorSession;
        try
        {
            var identity = await client.GetProgramMd5Async();
            var result = await client.ReadVariablesAsync(debugSymbols.Select(s => s.Address).ToArray());
            if (!ReferenceEquals(client, debugClient) || !ReferenceEquals(session, monitorSession)) return;
            if (identity.Md5 != activeProgramMd5) throw new InvalidDataException("设备程序身份已改变，请重新绑定");
            var now = DateTimeOffset.UtcNow; var sequence = ++monitorSequence;
            for (var index = 0; index < debugSymbols.Count; index++)
            {
                var symbol = debugSymbols[index]; var value = DebugValueDecoder.Decode(symbol.Type, result.Values[index].Bytes.Span, debugLittleEndian);
                session.Observe(session.Identity, new(symbol.Path, symbol.Type, value, sequence, null, now, value is null ? SampleQuality.UnsupportedType : SampleQuality.Good));
            }
            monitorExplanation.Text = $"真实只读采样 · 设备 tick {result.Tick} · 收到时间 {now.ToLocalTime():HH:mm:ss.fff}\n身份：{candidatePackage!.BuildHash}\n时间类型当前显示设备原始计数；尚不支持的类型显示不可用。";
            RefreshInstanceTree(); RefreshTrend();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(client, debugClient))
            {
                await DisconnectDebugAsync();
                monitorExplanation.Text = "变量监视已断开。断开原因：" + (error is ObjectDisposedException ? "调试连接已关闭，请重新绑定。" : error.Message);
            }
            throw;
        }
        finally { polling = false; }
    }
    private string? activeProgramMd5;
    private void RefreshInstanceTree()
    {
        RefreshInlineMonitor();
        var roots = new List<TreeViewItem>();
        foreach (var symbol in debugSymbols)
        {
            var parts = symbol.Path.Split('.'); var children = roots;
            for (var i = 0; i < parts.Length; i++)
            {
                var path = string.Join('.', parts.Take(i + 1));
                var node = children.FirstOrDefault(n => (string?)n.Tag == path);
                if (node is null) { node = new TreeViewItem { Header = parts[i], Tag = path, IsExpanded = true, ItemsSource = new List<TreeViewItem>() }; children.Add(node); }
                if (i == parts.Length - 1) { var sample = monitorSession?.GetSample(symbol.Path, DateTimeOffset.UtcNow); node.Header = $"{parts[i]} : {symbol.Type} = {sample?.Value ?? "—"}  [{sample?.Quality.ToString() ?? "Unavailable"}]"; }
                children = (List<TreeViewItem>)node.ItemsSource!;
            }
        }
        instanceTree.ItemsSource = roots;
    }
    private void RefreshInlineMonitor()
    {
        if (refreshingInline) return;
        refreshingInline = true;
        try
        {
            var paths = debugSymbols.Select(s => s.Path).ToArray();
            var program = project.Items.FirstOrDefault(i => i.Id == project.Tasks.FirstOrDefault(t => t.Enabled)?.ProgramId)?.Name;
            var doc = selected is null ? null : sources.GetValueOrDefault(selected.Id);
            var owners = doc is null ? [] : language.Index([doc]).Where(s => s.Kind is "PROGRAM" or "FUNCTION_BLOCK").Select(s => s.Name).ToArray();
            var contexts = program is null ? [] : language.MonitorContexts(GetSources(), program, paths).Where(c => owners.Contains(c.Owner, StringComparer.OrdinalIgnoreCase)).ToArray();
            var previous = monitorInstance.SelectedItem as StMonitorContext;
            var existing = (monitorInstance.ItemsSource as IEnumerable<StMonitorContext>)?.ToArray() ?? [];
            if (!existing.SequenceEqual(contexts))
            {
                monitorInstance.ItemsSource = contexts;
                // Several FB instances require an explicit selection; never silently choose one.
                monitorInstance.SelectedItem = contexts.FirstOrDefault(c => c == previous) ?? (contexts.Length == 1 ? contexts[0] : null);
            }
            var context = monitorInstance.SelectedItem as StMonitorContext;
            var now = DateTimeOffset.UtcNow;
            var usable = debugClient is not null && monitorSession is not null && CandidateMatchesProject();
            inlineMonitor.Values = doc is null || context is null ? [] : language.MonitorReferences(doc, context, paths).Select(r =>
            {
                var sample = usable ? monitorSession!.GetSample(r.Path, now) : null;
                var quality = sample?.Quality ?? SampleQuality.Unavailable;
                return new InlineMonitorValue(r.EndOffset, r.Path, quality == SampleQuality.Good ? sample!.Value ?? "不可用" : quality == SampleQuality.Expired ? "已过期" : "不可用", quality);
            }).ToArray();
            var received = usable && inlineMonitor.Values.Count > 0 ? monitorSession!.GetSample(inlineMonitor.Values[0].Path, now).ReceivedAt.ToLocalTime().ToString("HH:mm:ss") : "—";
            var qualityLabel = inlineMonitor.Values.Any(v => v.Quality == SampleQuality.Expired) ? "样本已过期" : "真实只读监视";
            inlineStatus.Text = !usable ? "离线 / 身份未绑定 · 数值不可用。先 SSH 连接并生成一致的候选包，再开启 ST 在线监视。" : context is null ? "真实只读监视已连接 · 请选择当前功能块实例；没有调试映射的对象不显示数值。" : $"{qualityLabel} · {context.Path} · 收到时间 {received} · 绿色 TRUE / 黄色 FALSE · 标记不写入源码";
            editor.TextArea.TextView.Redraw();
            RefreshPathExecutionStatus();
        }
        finally { refreshingInline = false; }
    }
    private void RefreshTrend()
    {
        trendView.Caption = trendPath is null ? "选择一个数值变量查看趋势" : $"{trendPath} · 最近 256 个采样 · 缺失时断线";
        trendView.Points = trendPath is null ? [] : monitorSession?.GetTrend(trendPath) ?? []; trendView.InvalidateVisual();
    }
    private async Task RefreshRuntimeAsync()
    {
        if (runtime is null) throw new InvalidOperationException("请先连接 Runtime"); refreshButton.IsEnabled = false; var connection = runtime; var generation = connectionGeneration;
        try { var status = await connection.GetStatusAsync(); if (generation != connectionGeneration) return; runtimeDetails.Text = $"查询时间 {DateTimeOffset.Now:HH:mm:ss}（手动刷新）\n" + JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }); var logs = await connection.GetLogsAsync(); if (generation == connectionGeneration) AppendLog(JsonSerializer.Serialize(logs), false); }
        catch { if (generation == connectionGeneration) runtimeDetails.Text = "通信失败，设备状态不可用。上一次查询结果不代表当前状态。"; throw; }
        finally { refreshButton.IsEnabled = runtime?.IsAuthenticated == true; }
    }
    private sealed class SymbolCompletion(StSymbol symbol) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => symbol.Name;
        public object Content => symbol.Name + (symbol.Type.Length > 0 ? "  : " + symbol.Type : "");
        public object Description => $"{symbol.Kind} · {symbol.Type}\n{symbol.File}:{symbol.Line}";
        public double Priority => 0;
        public void Complete(TextArea area, ISegment segment, EventArgs insertionRequestEventArgs)
        {
            area.Document.Replace(segment, symbol.Name);
        }
    }
}







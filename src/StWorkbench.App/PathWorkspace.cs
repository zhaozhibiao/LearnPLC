using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using StWorkbench.Core;
using StWorkbench.Motion;
using StWorkbench.OpenPlc;
using PathSegment = StWorkbench.Core.PathSegment;

namespace StWorkbench.App;

public sealed record PathResourceReference(Guid Id, uint Version);

public sealed partial class MainWindow
{
    private readonly PathPreview pathPreview = new() { Height = 230, MinHeight = 170 };
    private readonly TextBlock pathStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A9BAD1") };
    private readonly TextBlock pathPlanningTime = new() { Text="预计运动时间：未计算", TextWrapping=TextWrapping.Wrap,FontSize=16,Foreground=Brush.Parse("#7ECED4") };
    private readonly TextBox pathName = new() { Text = "Path1" };
    private readonly TextBox pathScale = new() { Text = "1" };
    private readonly TextBox pathOriginalUnit = new() { Text = "未指定", Width = 130 };
    private readonly TextBox[] pathOffsets = [new() { Text = "0" }, new() { Text = "0" }, new() { Text = "0" }];
    private readonly TextBox[] pathProfile = [new() { Text = "5" }, new() { Text = "20" }, new() { Text = "20" }, new() { Text = "100" }, new() { Text = "0.05" }];
    private readonly ComboBox[] pathAxes = [new(), new(), new()];
    private readonly ComboBox pathContours = new();
    private MotionPathAsset? selectedPath;
    private IReadOnlyList<DxfContour> importedContours = [];
    private string pathSourceName = "";
    private Control? pathPane;
    private bool pathImporting;

    private Control PathPane()
    {
        if (pathPane is not null) return pathPane;
        Register(pathPreview, "PathPreview"); Register(pathStatus, "PathStatus"); Register(pathName, "PathName"); Register(pathContours, "PathContours"); Register(pathScale, "PathScale"); Register(pathOriginalUnit, "PathOriginalUnit");Register(pathPlanningTime,"PathPlanningTime");
        var panel = new StackPanel { Spacing = 9, Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "路径资源 · 连续轮廓", FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(ActionButton("导入 DXF", ImportPathDxfAsync));
        panel.Children.Add(new TextBlock { Text = "一条资源对应一条连续轮廓；黄色点是起点。轴必须已配置且位于起点，预存和下载不会启动运动。", TextWrapping = TextWrapping.Wrap });
        var fields = new StackPanel { Spacing = 9 };
        var geometry = new Grid { ColumnDefinitions = new ColumnDefinitions("*,260"), ColumnSpacing = 12 };
        geometry.Children.Add(fields);
        void Row(string label, params Control[] controls)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = label, Width = 155, VerticalAlignment = VerticalAlignment.Center });
            foreach (var control in controls) { control.MinWidth = 105; control.Margin = new Thickness(0, 0, 6, 0); row.Children.Add(control); }
            fields.Children.Add(row);
        }
        Row("ST 名称 / 轮廓", pathName, pathContours); Row("原始单位（备注）", pathOriginalUnit);Row("转 mm 倍率", pathScale);
        fields.Children.Add(new TextBlock{Text="支持 10 或 10:1（目标:原始），都表示原始坐标乘以 10；原始单位备注不自动换算。",TextWrapping=TextWrapping.Wrap});
        Row("X / Y / Z 工程轴", pathAxes); Row("X / Y / Z 偏移 (mm)", pathOffsets);
        Row("速度 / 加速 / 减速", pathProfile[..3]); Row("跃度 / 轮廓容差 (mm)", pathProfile[3..]);
        var preview = new StackPanel { Spacing = 7, Children = { new TextBlock { Text = "XY 投影：蓝色为原几何，绿色为规划轨迹。零容差尖角可能降至零速。", TextWrapping = TextWrapping.Wrap }, pathPreview } };
        Grid.SetColumn(preview, 1); geometry.Children.Add(preview); panel.Children.Add(geometry);
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("预览 / 离线检查", () => { PreviewPathDraft(); return Task.CompletedTask; }));
        var prestore = ActionButton("预存工程不可变新版本", () => { AddPathAsset(ReadPathDraft()); return Task.CompletedTask; }); Register(prestore, "PathPrestore"); actions.Children.Add(prestore); panel.Children.Add(actions);panel.Children.Add(pathPlanningTime); panel.Children.Add(pathStatus);
        foreach(var field in new[]{pathName,pathScale,pathOriginalUnit}.Concat(pathOffsets).Concat(pathProfile))
            field.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty)InvalidatePathPlanningTime();};
        foreach(var axis in pathAxes)axis.PropertyChanged+=(_,e)=>{if(e.Property==ComboBox.SelectedItemProperty)InvalidatePathPlanningTime();};
        pathContours.SelectionChanged += (_, _) => { if (importedContours.Count > 0 && pathContours.SelectedIndex >= 0) { selectedPath = null; PreviewImportedGeometry(); } };
        pathPane = new ScrollViewer { Content = panel }; RefreshPathAxes(); SetPathOfflineStatus(); return pathPane;
    }
    private void RefreshPathAxes()
    {
        var axes = project.Hardware.Axes.Select(a => a.Name).ToArray();
        for (int i = 0; i < 3; i++) { var previous = pathAxes[i].SelectedItem as string; pathAxes[i].ItemsSource = i == 2 ? new[] { "（不使用 Z）" }.Concat(axes).ToArray() : axes; pathAxes[i].SelectedItem = previous ?? (i == 2 ? "（不使用 Z）" : axes.ElementAtOrDefault(i)); }
    }
    private void SetPathOfflineStatus(string detail = "尚未预存路径") => pathStatus.Text = $"离线：{detail}\n部署：路径清单与已激活程序尚未确认\n虚拟执行：尚无执行反馈；规划结果不代表实际轴反馈。";
    private void InvalidatePathPlanningTime()
    {
        if(pathPane is null||selectedPath is null&&importedContours.Count==0)return;
        pathPlanningTime.Text="预计运动时间：参数已修改，待重新计算";
        SetPathOfflineStatus("参数已修改，旧规划时间已失效；请点击“预览 / 离线检查”。");
    }
    private void SetPathPlannedTime(PreparedTrajectory plan)=>pathPlanningTime.Text=$"预计运动时间（离线规划）：{plan.DurationSeconds:F3}s";
    private TreeViewItem[] PathNodes() => project.Paths.Select(p => new TreeViewItem { Header = p.ToString(), Tag = new PathResourceReference(p.Id, p.Version), ContextMenu = PathAssetMenu(p) }).ToArray();
    private TreeViewItem PathResourceGroupNode()
    {
        var node = new TreeViewItem { Header = "路径资源", Tag = "path-resources", IsExpanded = true, ContextMenu = PathGroupMenu(), ItemsSource = PathNodes() };
        // SelectionChanged does not fire when returning to an already selected group.
        node.Tapped += (_, e) =>
        {
            if (e.Source is Control source && ReferenceEquals(source as TreeViewItem ?? source.FindAncestorOfType<TreeViewItem>(), node))
                ShowPathWorkspace();
        };
        return node;
    }
    private ContextMenu PathGroupMenu()
    {
        var import = new MenuItem { Header = "导入 DXF" }; import.Click += async (_, _) => await Run(ImportPathDxfAsync); return new ContextMenu { ItemsSource = new[] { import } };
    }
    private ContextMenu PathAssetMenu(MotionPathAsset asset)
    {
        var open = new MenuItem { Header = "打开 / 属性" }; open.Click += (_, _) => SelectPath(asset.Id, asset.Version);
        var version = new MenuItem { Header = "生成新版本" }; version.Click += (_, _) => { SelectPath(asset.Id, asset.Version); SetPathOfflineStatus("编辑参数后点击预存；原版本保持不变"); };
        var delete = new MenuItem { Header = "删除…" }; delete.Click += async (_, _) => await Run(async () =>
        {
            if (busy) return; var targetProject = project;
            if (!await ConfirmAsync("删除路径版本", $"删除 {asset.Name} v{asset.Version}？", "删除") || busy) return;
            if (!ReferenceEquals(targetProject, project) || !project.Paths.Contains(asset)) throw new InvalidOperationException("工程或路径已改变，请重新选择后删除");
            DeletePathAsset(asset.Id, asset.Version);
        });
        return new ContextMenu { ItemsSource = new[] { open, version, delete } };
    }
    public void SelectPath(Guid id, uint version)
    {
        var asset = project.Paths.Single(p => p.Id == id && p.Version == version); PathPane(); selectedPath = asset; importedContours = []; pathContours.ItemsSource = new[] { asset.Name + " · " + asset.Layer }; pathContours.SelectedIndex = 0;
        pathName.Text = asset.Name; pathSourceName = asset.SourceName; pathScale.Text = asset.ImportScale.ToString(CultureInfo.InvariantCulture); pathOriginalUnit.Text = asset.OriginalUnit; RefreshPathAxes();
        for (int i = 0; i < 3; i++) pathAxes[i].SelectedItem = i < asset.Axes.Length ? asset.Axes[i] : "（不使用 Z）";
        double[] offsets = [asset.Offset.X, asset.Offset.Y, asset.Offset.Z]; double[] profile = [asset.Profile.Velocity, asset.Profile.Acceleration, asset.Profile.Deceleration, asset.Profile.Jerk, asset.Profile.CornerTolerance];
        for (int i = 0; i < 3; i++) pathOffsets[i].Text = offsets[i].ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < 5; i++) pathProfile[i].Text = profile[i].ToString(CultureInfo.InvariantCulture);
        pathPreview.SetPath(asset.Segments, asset.Offset);pathPlanningTime.Text="预计运动时间：请点击预览重新计算"; SetPathOfflineStatus($"工程预存 {asset.Name} v{asset.Version} · {asset.ContentHash}"); RefreshPathExecutionStatus(); ShowPathWorkspace();
    }
    public void RefreshPaths() { selectedPath = null; importedContours = []; if (pathPane is not null) { pathContours.ItemsSource = Array.Empty<string>(); pathPreview.SetPath([], default);pathPlanningTime.Text="预计运动时间：未计算"; RefreshPathAxes(); SetPathOfflineStatus(); } RefreshInstructionLibrary(); }
    public void AddPathAsset(MotionPathAsset asset)
    {
        if (busy || pathImporting) throw new InvalidOperationException("当前操作未结束");
        if (asset.Axes is null || asset.Segments is null || asset.Profile is null) throw new InvalidDataException("路径资源结构无效");
        if (!string.IsNullOrEmpty(asset.ContentHash) && !MotionPathAsset.CalculateHash(asset).Equals(asset.ContentHash, StringComparison.Ordinal)) throw new InvalidDataException("路径内容摘要不匹配");
        if (project.Paths.Any(p => p.Id == asset.Id && p.Version == asset.Version)) throw new InvalidDataException("已预存版本不可覆盖，请生成新版本");
        if (project.Paths.Any(p => p.Id == asset.Id && p.Version >= asset.Version)) throw new InvalidDataException("新版本号必须大于所有已预存版本");
        var sealedAsset = (asset with { Axes = asset.Axes.ToArray(), Segments = asset.Segments.ToArray() }).Seal();
        var errors = MotionPathAsset.Validate(sealedAsset); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        var planned = ContinuousPathPlanner.Prepare(sealedAsset, project.Hardware.Axes);
        project.Paths.Add(sealedAsset);
        try
        {
            var projectErrors = ProjectValidator.Validate(project, GetSources());
            if (projectErrors.Count > 0) throw new InvalidDataException(string.Join("\n", projectErrors.Select(e => e.Message)));
        }
        catch { project.Paths.Remove(sealedAsset); throw; }
        MarkDirty(); RefreshTree(); RefreshInstructionLibrary(); SelectPath(sealedAsset.Id, sealedAsset.Version);
        pathPreview.SetPath(sealedAsset.Segments, sealedAsset.Offset, planned.Samples.Select(s => s.Position));
        SetPathPlannedTime(planned);
        SetPathOfflineStatus($"已预存 {sealedAsset.Name} v{sealedAsset.Version} · 离线规划 {planned.DurationSeconds:F3}s · {sealedAsset.ContentHash}");
    }
    public void DeletePathAsset(Guid id, uint version)
    {
        var asset = project.Paths.Single(p => p.Id == id && p.Version == version);
        var referenced = new List<MotionPathAsset>();
        if (PathBindingValidator.Validate(GetSources(), project.Paths, referenced).Count > 0) throw new InvalidDataException("路径引用检查尚未通过，请先修复源码再删除。");
        if (referenced.Any(p => p.Id == id && p.Version == version)) throw new InvalidDataException("ST 正在引用此路径版本，请先移除引用再删除。");
        if (busy) throw new InvalidOperationException("当前操作未结束"); project.Paths.Remove(asset); MarkDirty(); RefreshTree(); RefreshPaths();
    }
    public IEnumerable<StInstruction> PathInstructions() => project.Paths.Select(PathInstruction);
    public static StInstruction PathInstruction(MotionPathAsset asset) => new($"Path_{asset.Id:N}_v{asset.Version}", $"{asset.Name} · v{asset.Version}", "工程路径", "绑定工程预存版本。Execute 上升沿请求；Busy / Done / Error / ErrorID / Progress / RequestId 来自执行反馈。", $"{{p}}(Execute := {{p}}Execute, Stop := {{p}}Stop, PathId := ULINT#{asset.NumericId}, Version := ULINT#{asset.Version}, ContentHash := '{asset.ContentHash}');", ["{p} : MC_ExecuteContinuousPath;", "{p}Execute : BOOL := FALSE;", "{p}Stop : BOOL := FALSE;"], Stateful: true) { InstanceNameHint = asset.Name };
    private void RefreshPathExecutionStatus()
    {
        if (pathPane is null || selectedPath is not { } asset) return;
        if (debugClient is null || monitorSession is null || !(engineeringClient is not null ? EngineeringOnlineIdentityReady() : CandidateMatchesProject())) { SetPathOfflineStatus($"工程预存 {asset.Name} v{asset.Version} · {asset.ContentHash}"); return; }
        var now = DateTimeOffset.UtcNow;
        string? Field(string prefix, string name)
        {
            var symbol = debugSymbols.FirstOrDefault(s => s.Path.Equals(prefix + "." + name, StringComparison.OrdinalIgnoreCase));
            var sample = symbol is null ? null : monitorSession.GetSample(symbol.Path, now);
            return sample?.Quality == SampleQuality.Good ? sample.Value : null;
        }
        var matches = new List<string>();
        foreach (var symbol in debugSymbols.Where(s => s.Path.EndsWith(".PATHID", StringComparison.OrdinalIgnoreCase)))
        {
            var prefix = symbol.Path[..symbol.Path.LastIndexOf('.')];
            if (Field(prefix, "PathId") != asset.NumericId.ToString(CultureInfo.InvariantCulture) || Field(prefix, "Version") != asset.Version.ToString(CultureInfo.InvariantCulture) || Field(prefix, "ContentHash")?.Trim('\'', '"') != asset.ContentHash) continue;
            matches.Add(prefix + "\n" + string.Join(" · ", new[] { "RequestId", "Execute", "Busy", "Done", "Error", "ErrorID", "Progress" }.Select(n => n + "=" + (Field(prefix, n) ?? "不可用 / 已过期"))));
        }
        pathStatus.Text = $"离线：工程预存 {asset.Name} v{asset.Version}\n部署：当前程序调试身份已验证\n执行：" + (matches.Count == 0 ? "未找到摘要、版本与 ID 全部匹配的实例反馈" : string.Join("\n", matches)) + "\n反馈来自同一 PLC 只读监视；虚拟后端反馈不能作为实体轴验收。";
    }
    private static double Number(TextBox field) => double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new InvalidDataException("请输入有限数值（小数点使用 .）");
    private MotionPathAsset ReadPathDraft()
    {
        PathSegment[] geometry; string layer;
        if (importedContours.Count > 0 && pathContours.SelectedIndex >= 0) { var c = importedContours[pathContours.SelectedIndex]; geometry = c.Segments; layer = c.Layer; } else if (selectedPath is { } existing) { geometry = existing.Segments; layer = existing.Layer; } else throw new InvalidDataException("请先导入并选择一条连续轮廓");
        double scale = ReadImportScale();
        var geometryScale = selectedPath is null ? scale : scale / selectedPath.ImportScale;
        PathPoint Scale(PathPoint p) => new(p.X * geometryScale, p.Y * geometryScale, p.Z * geometryScale);
        var id = selectedPath?.Id ?? Guid.NewGuid(); var version = selectedPath is null ? 1u : checked(project.Paths.Where(p => p.Id == id).Max(p => p.Version) + 1);
        return new() { Id = id, Version = version, Name = pathName.Text?.Trim() ?? "", SourceName = pathSourceName, Layer = layer, ImportScale = scale, OriginalUnit = pathOriginalUnit.Text?.Trim() ?? "未指定", Axes = pathAxes.Where((c, i) => i < 2 || c.SelectedItem as string != "（不使用 Z）").Select(c => c.SelectedItem as string ?? "").ToArray(), Segments = geometry.Select(s => s with { Start = Scale(s.Start), End = Scale(s.End), Center = Scale(s.Center) }).ToArray(), Offset = new(Number(pathOffsets[0]), Number(pathOffsets[1]), Number(pathOffsets[2])), Profile = new(Number(pathProfile[0]), Number(pathProfile[1]), Number(pathProfile[2]), Number(pathProfile[3]), Number(pathProfile[4])) };
    }
    private void PreviewImportedGeometry() { var c = importedContours[pathContours.SelectedIndex]; pathPreview.SetPath(c.Segments, default);pathPlanningTime.Text="预计运动时间：未计算"; SetPathOfflineStatus($"已导入 {importedContours.Count} 条独立轮廓；选择后检查和预存"); }
    public void ImportPathText(string sourceName, string text)
    {
        if (busy || pathImporting) throw new InvalidOperationException("当前操作未结束");
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("DXF 超过 8 MiB 导入上限");
        ApplyImportedContours(sourceName, DxfPathImporter.Parse(text));
    }
    public void PrestorePathDraft() => AddPathAsset(ReadPathDraft());
    private void ApplyImportedContours(string sourceName, IReadOnlyList<DxfContour> contours)
    {
        if (contours.Count == 0) throw new InvalidDataException("DXF 不包含可导入的连续轮廓");
        if (contours.Any(c => c.Segments.Length > 4096)) throw new InvalidDataException("单条连续轮廓超过 4096 段工程预存上限，请缩减几何后重新导入");
        PathPane(); selectedPath = null; importedContours = contours; pathSourceName = sourceName; pathName.Text = "Path" + (project.Paths.Select(p => p.Id).Distinct().Count() + 1); pathScale.Text = "1"; pathOriginalUnit.Text = "未指定";
        foreach (var offset in pathOffsets) offset.Text = "0";
        RefreshPathAxes(); pathContours.ItemsSource = contours.Select(c => c.Name + " · " + c.Layer).ToArray(); pathContours.SelectedIndex = 0; PreviewImportedGeometry(); ShowPathWorkspace();
    }
    private double ReadImportScale()
    {
        var parts=(pathScale.Text??"").Replace('：',':').Split(':');
        bool Positive(string text,out double value)=>double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)&&double.IsFinite(value)&&value>0;
        if(parts.Length is <1 or >2||!Positive(parts[0],out var numerator))throw new InvalidDataException("倍率请填正数或比例，例如 10、10:1（目标:原始）。");
        double denominator=1;
        if(parts.Length==2&&!Positive(parts[1],out denominator))throw new InvalidDataException("比例分母必须是正数，例如 10:1。");
        var scale=numerator/denominator;
        if(!double.IsFinite(scale)||scale<=0||scale>1e6)throw new InvalidDataException("单位倍率必须大于零且不超过 1000000。");
        return scale;
    }
    private void PreviewPathDraft()
    {
        try
        {
            var asset=ReadPathDraft().Seal();var errors=MotionPathAsset.Validate(asset);if(errors.Count>0)throw new InvalidDataException(string.Join("\n",errors));
            var planned=ContinuousPathPlanner.Prepare(asset,project.Hardware.Axes);pathPreview.SetPath(asset.Segments,asset.Offset,planned.Samples.Select(s=>s.Position));SetPathPlannedTime(planned);
            SetPathOfflineStatus($"检查通过 · {planned.DurationSeconds:F3}s；尚未预存当前草稿");
        }
        catch(Exception error)
        {
            pathPlanningTime.Text="预计运动时间：无法计算";
            var reason=error.Message.Contains("position bounds",StringComparison.Ordinal)?"轨迹超出工程轴限位，请检查倍率、偏移和轴行程。":error.Message.Contains("time/sample budget",StringComparison.Ordinal)?"规划超过时长或采样上限（最多 600 秒 / 250000 点），请拆分轮廓或调整参数。":error.Message;
            SetPathOfflineStatus("规划失败："+reason+"；没有有效规划时间。");
            throw;
        }
    }
    private async Task ImportPathDxfAsync()
    {
        if (busy || pathImporting) throw new InvalidOperationException("当前操作未结束");
        var generation = editGeneration; var importingProject = project;
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "导入 ASCII DXF（单文件）", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("DXF") { Patterns = ["*.dxf"] }] });
        if (files.Count != 1) return;
        if (busy || generation != editGeneration || !ReferenceEquals(project, importingProject)) throw new InvalidOperationException("选择文件期间工程已变更，请重新导入");
        pathImporting = true;
        try
        {
            await using var stream = await files[0].OpenReadAsync(); using var reader = new StreamReader(stream, Encoding.UTF8, true); var text = new StringBuilder(); var buffer = new char[8192]; int count;
            while ((count = await reader.ReadAsync(buffer)) > 0) { if (text.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("DXF 超过 8 MiB 导入上限"); text.Append(buffer, 0, count); }
            var contours = await Task.Run(() => DxfPathImporter.Parse(text.ToString()));
            if (busy || generation != editGeneration || !ReferenceEquals(project, importingProject)) throw new InvalidOperationException("导入期间工程已变更，请重新导入");
            ApplyImportedContours(files[0].Name, contours);
        }
        finally { pathImporting = false; }
    }
}

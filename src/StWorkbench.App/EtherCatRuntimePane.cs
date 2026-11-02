using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using StWorkbench.Core;
using StWorkbench.Toolchain;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private readonly CheckBox genericEtherCat = new() { Content = "启用通用 EtherCAT 运行组态", IsChecked = false };
    private readonly TextBox etherCatCycle = new() { Text = "1000", Width = 110 };
    private readonly ComboBox stationRuntimeProfile = new() { Width = 190, ItemsSource = new[] { "IO", "cia402-csp", "is620n-csp" }, SelectedIndex = 0 };
    private readonly CheckBox etherCatCycleTimingEnabled = new() { Content = "启用R2M时序保护（未定沿用旧工程）", IsThreeState = true, IsChecked = null };
    private readonly TextBox cycleBoundaryGuard = new() { Text = "200000", Width = 120 }, cycleSendBudget = new() { Text = "100000", Width = 120 }, cycleGoodCycles = new() { Text = "1000", Width = 100 };
    private readonly TextBox stationDcShift = new() { Width = 150, PlaceholderText = "沿用ESI" };
    private readonly ComboBox stationDcMode = new() { Width = 260 };
    private readonly StackPanel stationRuntimePdos = new() { Spacing = 3 };
    private readonly TextBlock etherCatRuntimeStatus = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly ComboBox runtimeAxisProfile = new() { Width = 190, ItemsSource = new[] { "cia402-csp", "is620n-csp" }, SelectedIndex = 0 };
    private readonly TextBox runtimeMotorModel = new() { Width = 220, PlaceholderText = "电机完整型号" };
    private readonly CheckBox runtimeMechanicsConfirmed = new() { Content = "已核实轴机械参数与反馈计数", IsChecked = false };
    private readonly CheckBox runtimeSafetyConfirmed = new() { Content = "已核实限位及保护参数", IsChecked = false };
    private readonly TextBox runtimeActualVelocity = new() { Width = 110, PlaceholderText = "实速保护上限" };
    private readonly TextBox runtimeFollowingError = new() { Width = 110, PlaceholderText = "跟随误差上限" };
    private readonly TextBox runtimeFollowingTimeout = new() { Width = 90, Text = "20" };
    private readonly TextBox runtimeHomeMethod = new() { Width = 80, Text = "0" };
    private readonly CheckBox runtimeCurrentZero = new() { Content = "允许方法 35 当前位置临时原点（仅 IS620N 适配）" };
    private readonly TextBox runtimeHomeSearch = new() { Width = 100, Text = "0" }, runtimeHomeLatch = new() { Width = 100, Text = "0" }, runtimeHomeAcceleration = new() { Width = 100, Text = "0" }, runtimeHomeOffset = new() { Width = 100, Text = "0" }, runtimeHomeTimeout = new() { Width = 100, Text = "30000" };
    private readonly TextBox runtimePositionTolerance = new() { Width = 100, Text = "0.001" }, runtimeVelocityTolerance = new() { Width = 100, Text = "0.001" }, runtimeCompletionDwell = new() { Width = 100, Text = "100" };
    private readonly CheckBox runtimeStablePosition = new() { Content = "使用位置稳定窗口判定到位" };

    private Control EtherCatRuntimePane()
    {
        var panel = new StackPanel { Spacing = 7 };
        Register(genericEtherCat, "GenericEtherCatEnabled"); Register(etherCatCycle, "EtherCatCycleMicroseconds"); Register(etherCatRuntimeStatus, "EtherCatRuntimeStatus");
        Register(stationRuntimeProfile, "StationRuntimeProfile"); Register(stationDcMode, "StationDcMode"); Register(stationRuntimePdos, "StationRuntimePdos");
        Register(etherCatCycleTimingEnabled, "EtherCatCycleTimingEnabled"); Register(stationDcShift, "StationDcSync0ShiftNanoseconds");
        Register(cycleBoundaryGuard, "EtherCatBoundaryGuardNs"); Register(cycleSendBudget, "EtherCatRemainingSendBudgetNs"); Register(cycleGoodCycles, "EtherCatRequiredGoodCycles");
        panel.Children.Add(genericEtherCat);
        panel.Children.Add(new WrapPanel { Children = { Label("总线 / 轴循环周期（μs）"), etherCatCycle } });
        panel.Children.Add(etherCatCycleTimingEnabled);
        panel.Children.Add(new WrapPanel { Children = { Label("边界裕量ns / 剩余发送预算ns / 连续就绪周期数"), cycleBoundaryGuard, cycleSendBudget, cycleGoodCycles } });
        var actions = new WrapPanel();
        AddConfigurationButton(actions, "应用周期设置到工程", "ApplyEtherCatRuntime", ApplyEtherCatRuntimeSettings);
        AddConfigurationButton(actions, "检查总线配置", "CheckEtherCatRuntime", CheckEtherCatRuntimeSettings);
        actions.Children.Add(ActionButton("导出总线组态包", ExportEtherCatRuntimeAsync));
        panel.Children.Add(actions);
        panel.Children.Add(Label("选中从站 · PDO 按完整组选择；IO 和轴仍在原映射区域配置。"));
        panel.Children.Add(new WrapPanel { Children = { Label("运行适配"), stationRuntimeProfile, Label("DC 模式"), stationDcMode } });
        panel.Children.Add(new WrapPanel { Children = { Label("选中从站Sync0偏移 / ns（空白沿用ESI；仅实机确认后覆盖）"), stationDcShift } });
        panel.Children.Add(stationRuntimePdos);
        panel.Children.Add(ActionButton("应用选中从站运行参数", () => { ApplyStationRuntimeSettings(); return Task.CompletedTask; }));
        panel.Children.Add(Label("选择轴适配后，该从站的 PDO 由轴核心独占；应用此设置会解除该从站的普通 IO 映射。SDO 启动写入、型号差异和单位必须通过配置检查，导入 XML 本身不代表设备就绪。"));
        panel.Children.Add(etherCatRuntimeStatus);
        return panel;
    }
    private Control EtherCatAxisRuntimePane()
    {
        var panel = new StackPanel { Spacing = 7 };
        Register(runtimeAxisProfile, "RuntimeAxisProfile"); Register(runtimeMotorModel, "RuntimeMotorModel");
        Register(runtimeMechanicsConfirmed, "RuntimeMechanicsConfirmed"); Register(runtimeSafetyConfirmed, "RuntimeSafetyConfirmed");
        panel.Children.Add(Label("选中工程轴 · 通用 CiA402 CSP 使用计数位置和计数/秒速度反馈。编码器、减速比、导程仍在上方机械属性中设置。"));
        panel.Children.Add(new WrapPanel { Children = { Label("轴适配"), runtimeAxisProfile, runtimeMotorModel } });
        panel.Children.Add(runtimeMechanicsConfirmed); panel.Children.Add(runtimeSafetyConfirmed);
        panel.Children.Add(new WrapPanel { Children = { Label("实际速度保护 / 跟随误差（工程单位） / 超时 ms"), runtimeActualVelocity, runtimeFollowingError, runtimeFollowingTimeout } });
        panel.Children.Add(new WrapPanel { Children = { Label("回零方法"), runtimeHomeMethod, runtimeCurrentZero } });
        panel.Children.Add(new WrapPanel { Children = { Label("回零搜索速度 / 慢速 / 加速度 / 偏移（原始计数）/ 超时 ms"), runtimeHomeSearch, runtimeHomeLatch, runtimeHomeAcceleration, runtimeHomeOffset, runtimeHomeTimeout } });
        panel.Children.Add(new WrapPanel { Children = { Label("到位位置公差 / 速度公差 / 持续 ms"), runtimePositionTolerance, runtimeVelocityTolerance, runtimeCompletionDwell } }); panel.Children.Add(runtimeStablePosition);
        AddMotionMigrationProperties(panel);
        panel.Children.Add(ActionButton("应用选中轴运行参数", () => { ApplyAxisRuntimeSettings(); return Task.CompletedTask; }));
        return panel;
    }
    public void ApplyEtherCatRuntimeSettings()
    {
        var previous = project.Hardware.Runtime;
        var next = genericEtherCat.IsChecked == true ? (previous ?? new EtherCatRuntimeSettings()) with { CycleTimeNanoseconds = checked(ReadUInt(etherCatCycle, "总线周期") * 1000U), CycleTiming = etherCatCycleTimingEnabled.IsChecked is { } enabled ? new() { Enabled = enabled, BoundaryGuardNs = ReadUInt(cycleBoundaryGuard, "边界裕量"), RemainingSendBudgetNs = ReadUInt(cycleSendBudget, "发送预算"), RequiredGoodCycles = ReadUInt(cycleGoodCycles, "连续就绪周期数") } : null } : null;
        ChangeHardware(() => project.Hardware.Runtime = next, () => project.Hardware.Runtime = previous);
    }
    public void CheckEtherCatRuntimeSettings()
    {
        var result = EtherCatRuntimeCompiler.Compile(project);
        etherCatRuntimeStatus.Text = result.ReadyForRuntime
            ? $"组态数据检查通过，尚未应用到设备。配置指纹：{result.TopologyFingerprint}"
            : "未满足运行配置：\n" + string.Join("\n", result.Blockers);
    }
    private void RefreshEtherCatRuntimeSettings()
    {
        genericEtherCat.IsChecked = project.Hardware.Runtime is not null;
        etherCatCycle.Text = ((project.Hardware.Runtime?.CycleTimeNanoseconds ?? 1_000_000) / 1000).ToString(CultureInfo.InvariantCulture);
        var timing = project.Hardware.Runtime?.CycleTiming;
        etherCatCycleTimingEnabled.IsChecked = timing?.Enabled;
        cycleBoundaryGuard.Text = (timing?.BoundaryGuardNs ?? 200000).ToString(CultureInfo.InvariantCulture); cycleSendBudget.Text = (timing?.RemainingSendBudgetNs ?? 100000).ToString(CultureInfo.InvariantCulture); cycleGoodCycles.Text = (timing?.RequiredGoodCycles ?? 1000).ToString(CultureInfo.InvariantCulture);
        var station = SelectedStation;
        stationDcShift.Text = station?.Runtime?.DcSync0ShiftNanoseconds?.ToString(CultureInfo.InvariantCulture) ?? "";
        var device = project.Hardware.Devices.FirstOrDefault(d => d.Id == station?.DeviceId);
        stationRuntimeProfile.SelectedItem = station?.Runtime?.ProfileId ?? "IO";
        var modes = new[] { "禁用 DC" }.Concat(device?.Description?.DcModes.Select(m => m.Name) ?? []).ToArray();
        stationDcMode.ItemsSource = modes; stationDcMode.SelectedItem = station?.Runtime?.DcModeName ?? modes[0];
        stationRuntimePdos.Children.Clear();
        foreach (var group in device?.Description?.PdoGroups ?? [])
            stationRuntimePdos.Children.Add(new CheckBox { Content = $"{(group.IsInput ? "Tx / 输入" : "Rx / 输出")} 0x{group.Index:X4} · SM{group.SyncManager} · {group.Name}{(group.Mandatory ? " · 必选" : "")}", Tag = group, IsChecked = station?.Runtime?.SelectedPdoIndices?.Contains(group.Index) ?? group.DefaultSelected });
        if (device is not null && device.Description is null)
            stationRuntimePdos.Children.Add(Label("该目录来自旧版工程，缺少 PDO 分组 / DC 信息；请重新导入原始 ESI。"));
        RefreshAxisRuntimeSettings();
    }
    public void ApplyStationRuntimeSettings()
    {
        var station = SelectedStation ?? throw new InvalidOperationException("请先选择工程从站。");
        var groups = stationRuntimePdos.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (EtherCatPdoGroup)c.Tag!).ToArray();
        var profile = stationRuntimeProfile.SelectedItem as string;
        var updated = station with { EnabledEntries = groups.SelectMany(g => g.Entries).Where(e => e.Index != 0).Distinct().ToArray(), Runtime = (station.Runtime ?? new EtherCatStationRuntimeSettings()) with { ProfileId = profile == "IO" ? null : profile, DcModeName = stationDcMode.SelectedItem is string mode && mode != "禁用 DC" ? mode : null, SelectedPdoIndices = groups.Select(g => g.Index).ToArray(), DcSync0ShiftNanoseconds = string.IsNullOrWhiteSpace(stationDcShift.Text) ? null : int.Parse(stationDcShift.Text, CultureInfo.InvariantCulture) } };
        var priorStations = project.Hardware.Stations; var priorIo = project.Hardware.Io;
        ChangeHardware(() => { project.Hardware.Stations = Stations.Select(s => s.Id == station.Id ? updated : s).ToList(); project.Hardware.Io = priorIo.Where(io => io.StationId != station.Id || profile == "IO" && updated.EnabledEntries.Any(e => e.Index == io.PdoIndex && e.SubIndex == io.PdoSubIndex && e.IsInput == io.IsInput)).ToList(); },
            () => { project.Hardware.Stations = priorStations; project.Hardware.Io = priorIo; });
        CheckEtherCatRuntimeSettings();
    }
    private void RefreshAxisRuntimeSettings()
    {
        var axis = project.Hardware.Axes.ElementAtOrDefault(axisList.SelectedIndex);
        var runtime = axis?.Runtime;
        LoadMotionMigrationProperties(runtime);
        runtimeAxisProfile.SelectedItem = runtime?.ProfileId ?? "cia402-csp"; runtimeMotorModel.Text = runtime?.MotorModel ?? "";
        runtimeMechanicsConfirmed.IsChecked = runtime?.MechanicsConfirmed == true; runtimeSafetyConfirmed.IsChecked = runtime?.SafetyConfirmed == true;
        static string N(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        runtimeActualVelocity.Text = N(runtime?.MaximumActualVelocity); runtimeFollowingError.Text = N(runtime?.MaximumFollowingError); runtimeFollowingTimeout.Text = (runtime?.FollowingErrorTimeoutMilliseconds ?? 20).ToString(CultureInfo.InvariantCulture);
        var home = runtime?.Homing; runtimeHomeMethod.Text = (home?.Method ?? 0).ToString(CultureInfo.InvariantCulture); runtimeCurrentZero.IsChecked = home?.AllowCurrentPositionZero == true;
        runtimeHomeSearch.Text = (home?.SearchSpeedRaw ?? 0).ToString(CultureInfo.InvariantCulture); runtimeHomeLatch.Text = (home?.LatchSpeedRaw ?? 0).ToString(CultureInfo.InvariantCulture); runtimeHomeAcceleration.Text = (home?.AccelerationRaw ?? 0).ToString(CultureInfo.InvariantCulture); runtimeHomeOffset.Text = (home?.OffsetRaw ?? 0).ToString(CultureInfo.InvariantCulture); runtimeHomeTimeout.Text = (home?.TimeoutMilliseconds ?? 30000).ToString(CultureInfo.InvariantCulture);
        runtimePositionTolerance.Text = N(runtime?.MotionCompletion?.PositionTolerance) is { Length: > 0 } pt ? pt : "0.001"; runtimeVelocityTolerance.Text = N(runtime?.MotionCompletion?.VelocityTolerance) is { Length: > 0 } vt ? vt : "0.001"; runtimeCompletionDwell.Text = (runtime?.MotionCompletion?.DwellMilliseconds ?? 100).ToString(CultureInfo.InvariantCulture); runtimeStablePosition.IsChecked = runtime?.MotionCompletion?.PositionStabilityCompletion == true;
    }
    public void ApplyAxisRuntimeSettings()
    {
        var index = axisList.SelectedIndex;
        if (index < 0 || index >= project.Hardware.Axes.Count) throw new InvalidOperationException("请先选择工程轴。");
        var axis = project.Hardware.Axes[index];
        var updated = axis with { Runtime = ApplyMotionMigrationProperties((axis.Runtime ?? new EtherCatAxisRuntimeSettings()) with { ProfileId = runtimeAxisProfile.SelectedItem as string ?? "cia402-csp", MotorModel = runtimeMotorModel.Text?.Trim() ?? "", MechanicsConfirmed = runtimeMechanicsConfirmed.IsChecked == true, SafetyConfirmed = runtimeSafetyConfirmed.IsChecked == true, MaximumActualVelocity = ReadFinite(runtimeActualVelocity, "实速保护"), MaximumFollowingError = ReadFinite(runtimeFollowingError, "跟随误差"), FollowingErrorTimeoutMilliseconds = checked((int)ReadUInt(runtimeFollowingTimeout, "跟随超时")), Homing = new() { Method = checked((int)ReadUInt(runtimeHomeMethod, "回零方法")), AllowCurrentPositionZero = runtimeCurrentZero.IsChecked == true, SearchSpeedRaw = ReadUInt(runtimeHomeSearch, "搜索速度"), LatchSpeedRaw = ReadUInt(runtimeHomeLatch, "回零慢速"), AccelerationRaw = ReadUInt(runtimeHomeAcceleration, "回零加速度"), OffsetRaw = int.Parse(runtimeHomeOffset.Text ?? "", CultureInfo.InvariantCulture), TimeoutMilliseconds = ReadUInt(runtimeHomeTimeout, "回零超时") }, MotionCompletion = new() { PositionTolerance = ReadFinite(runtimePositionTolerance, "位置公差"), VelocityTolerance = ReadFinite(runtimeVelocityTolerance, "速度公差"), DwellMilliseconds = ReadUInt(runtimeCompletionDwell, "到位持续时间"), PositionStabilityCompletion = runtimeStablePosition.IsChecked == true } }) };
        ChangeHardware(() => project.Hardware.Axes[index] = updated, () => project.Hardware.Axes[index] = axis);
        axisList.SelectedIndex = index;
        LoadAxisProperties();
        CheckEtherCatRuntimeSettings();
    }
    private static uint ReadUInt(TextBox box, string name) => uint.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException(name + "必须为非负整数。");
    private static double ReadFinite(TextBox box, string name) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new InvalidDataException(name + "必须为有限数值。");
    private async Task ExportEtherCatRuntimeAsync()
    {
        var result = EtherCatRuntimeCompiler.Compile(project);
        if (!result.ReadyForRuntime) { CheckEtherCatRuntimeSettings(); throw new InvalidDataException("总线配置未通过检查，请查看硬件组态提示。"); }
        var directory = Path.Combine(WorkbenchSettings.WorkspaceRoot, "artifacts", "hardware-configurations", result.TopologyFingerprint);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "ethercat-runtime.json"), result.TopologyJson, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(directory, "machine-config.json"), result.MachineJson, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(directory, "io-map.json"), result.IoMapJson, new UTF8Encoding(false));
        etherCatRuntimeStatus.Text = "已导出组态包，尚未应用：" + directory; AppendLog(etherCatRuntimeStatus.Text, false);
    }
    private Task ApplyEtherCatHardwareAsync() => DownloadHardwareAsync();
}

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private readonly ListBox configuredStations = new() { Height = 160 };
    private readonly TextBox stationName = new() { PlaceholderText = "从站名称，例如 DriveX", Width = 180 };
    private readonly TextBox stationAlias = new() { Text = "0", Width = 100 };
    private readonly StackPanel stationPdos = new() { Spacing = 3 };
    private readonly TextBlock hardwareConfigurationStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#7ECED4") };
    private Guid? selectedStationId;
    private bool refreshingConfiguration;
    private sealed record StationChoice(Guid? Id, string Name) { public override string ToString() => Name; }
    private sealed record PdoChoice(PdoEntry Entry) { public override string ToString() => PdoLabel(Entry); }
    private readonly ComboBox ioStation = new() { Width = 200 }, ioPdo = new() { Width = 280 }, axisStation = new() { Width = 200 };
    private readonly TextBox ioNameEditor = new() { PlaceholderText = "IO 变量名", Width = 140 }, ioAddressEditor = new() { PlaceholderText = "%IX0.0", Width = 140 };
    private readonly ComboBox ioTypeEditor = new() { Width = 115, ItemsSource = new[] { "BOOL", "BYTE", "WORD", "DWORD", "INT", "DINT", "REAL", "LREAL" }, SelectedIndex = 0 };
    private readonly ComboBox ioDirectionEditor = new() { Width = 100, ItemsSource = new[] { "输入", "输出" }, SelectedIndex = 0 };
    private readonly TextBox axisNameEditor = new() { PlaceholderText = "逻辑轴名", Width = 140 }, controllerAxisEditor = new() { PlaceholderText = "控制器轴名", Width = 140 }, axisUnitEditor = new() { Text = "mm", Width = 80 };
    private readonly TextBox axisMinEditor = new() { Text = "-1000", Width = 100 }, axisMaxEditor = new() { Text = "1000", Width = 100 };
    private readonly TextBox[] axisLimitEditors = new[] { "速度上限", "加速度上限", "减速度上限", "跃度上限" }.Select(s => new TextBox { PlaceholderText = s + "（可选）", Width = 130 }).ToArray();
    private static string PdoLabel(PdoEntry e) => $"{(e.IsInput ? "输入" : "输出")} 0x{e.Index:X4}:{e.SubIndex} {e.Name} · {e.Type} / {e.BitLength} bit";
    private IReadOnlyList<EtherCatStation> Stations => project.Hardware.Stations ?? [];
    private EtherCatStation? SelectedStation => Stations.FirstOrDefault(s => s.Id == selectedStationId);

    private Control HardwareConfigurationPane()
    {
        Register(deviceList, "DeviceCatalog"); Register(configuredStations, "ConfiguredStations"); Register(stationName, "StationName"); Register(stationAlias, "StationAlias"); Register(stationPdos, "StationPdos"); Register(hardwareConfigurationStatus, "HardwareConfigurationStatus");
        var panel = new StackPanel { Spacing = 9 };
        panel.Children.Add(new TextBlock { Text = "硬件组态", FontSize = 22, FontWeight = FontWeight.Bold });
        panel.Children.Add(Label("ESI 是设备目录；加入工程才成为组态从站。当前编辑的是离线配置，未扫描或写入物理设备。"));
        var lists = new Grid { ColumnDefinitions = new("*,12,*") };
        var catalog = new StackPanel { Spacing = 6, Children = { Label("ESI 设备目录"), deviceList, ActionButton("导入 ESI 文件", ImportEsiAsync), ActionButton("移除目录设备", () => { RemoveCatalogDevice(); return Task.CompletedTask; }) } };
        var stations = new StackPanel { Spacing = 6, Children = { Label("工程从站（连接顺序）"), configuredStations } };
        lists.Children.Add(catalog); Grid.SetColumn(stations, 2); lists.Children.Add(stations); panel.Children.Add(lists);
        deviceList.SelectionChanged += (_, _) => { pdoList.ItemsSource = deviceList.SelectedIndex >= 0 && deviceList.SelectedIndex < project.Hardware.Devices.Count ? project.Hardware.Devices[deviceList.SelectedIndex].Entries.Select(PdoLabel).ToArray() : []; };
        panel.Children.Add(new Expander { Header = "目录设备 PDO", Content = pdoList });
        panel.Children.Add(new WrapPanel { Children = { Label("从站属性"), stationName, Label("别名地址（0=未设置）"), stationAlias } });
        var actions = new WrapPanel();
        AddConfigurationButton(actions, "加入工程从站", "AddStationButton", () => AddConfiguredStation());
        AddConfigurationButton(actions, "应用从站属性 / PDO", "ApplyStationButton", () => ApplyStationProperties());
        AddConfigurationButton(actions, "补全 IO 映射", "CompleteStationIoButton", () => CompleteStationIoMappings());
        AddConfigurationButton(actions, "删除从站", "RemoveStationButton", () => RemoveConfiguredStation());
        AddConfigurationButton(actions, "上移", "HardwareStationUp", () => MoveConfiguredStation(-1));
        AddConfigurationButton(actions, "下移", "HardwareStationDown", () => MoveConfiguredStation(1)); panel.Children.Add(actions);
        configuredStations.SelectionChanged += (_, _) => { if (!refreshingConfiguration) SelectConfiguredStation(configuredStations.SelectedIndex); };
        panel.Children.Add(new Expander { Header = "选中从站的 PDO（勾选后应用）", IsExpanded = true, Content = stationPdos });
        panel.Children.Add(hardwareConfigurationStatus);
        panel.Children.Add(new Expander { Header = "通信通道 · Modbus / RS485 / RS232 / TCP", Content = CommunicationPane() });
        hardwareIoAxisExpander.Content = HardwareIoAxisPane(); Register(hardwareIoAxisExpander, "HardwareIoAxisExpander"); panel.Children.Add(hardwareIoAxisExpander);
        var scroll=ScrollPanel(panel);Register(scroll,"HardwareConfigurationScroll");return scroll;
    }
    private void AddConfigurationButton(Panel panel, string label, string name, Action action)
    {
        var button = ActionButton(label, () => { action(); return Task.CompletedTask; }); Register(button, name); panel.Children.Add(button);
    }
    private void ChangeHardware(Action change, Action rollback)
    {
        if (busy || deploymentBusy) throw new InvalidOperationException("当前工程或设备操作尚未结束。");
        try { change(); _ = ProjectValidator.Capture(project, GetSources()); }
        catch(Exception error)
        {
            rollback();
            hardwareConfigurationStatus.Text="未应用："+error.Message;
            hardwareConfigurationStatus.Foreground=Brush.Parse("#FF9B9B");
            throw;
        }
        MarkDirty(); RefreshHardware(); RefreshPaths();
        hardwareConfigurationStatus.Text = "工程组态已更新，尚未应用到设备；请保存并重新生成候选。";
        hardwareConfigurationStatus.Foreground=Brush.Parse("#7ECED4");
    }
    public void AddConfiguredStation()
    {
        if (deviceList.SelectedIndex < 0 || deviceList.SelectedIndex >= project.Hardware.Devices.Count) throw new InvalidOperationException("请先选择 ESI 目录中的设备。");
        var device = project.Hardware.Devices[deviceList.SelectedIndex];
        var name = stationName.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { var n = 1; while (Stations.Any(s => s.Name.Equals("Slave" + n, StringComparison.OrdinalIgnoreCase))) n++; name = "Slave" + n; }
        if (!ushort.TryParse(stationAlias.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var alias)) throw new InvalidDataException("从站别名地址应为 0–65535 的整数。");
        var station = new EtherCatStation(Guid.NewGuid(), name, device.Id, alias, device.Entries.DistinctBy(e => (e.Index,e.SubIndex,e.IsInput)).ToArray());
        var previous = project.Hardware.Stations;
        var plan = IoAutoMapping.Plan(project.Hardware, station);
        var previousIo = project.Hardware.Io;
        ChangeHardware(() => { project.Hardware.Stations = [.. Stations, station]; project.Hardware.Io = [.. previousIo, .. plan.Added]; },
            () => { project.Hardware.Stations = previous; project.Hardware.Io = previousIo; });
        selectedStationId = station.Id; RefreshConfigurationViews();
        ReportIoMappingPlan(plan);
    }
    public void ApplyStationProperties()
    {
        var station = SelectedStation ?? throw new InvalidOperationException("请先选择工程从站。");
        if (!ushort.TryParse(stationAlias.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var alias)) throw new InvalidDataException("从站别名地址应为 0–65535 的整数。");
        var entries = stationPdos.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (PdoEntry)c.Tag!).ToArray();
        var updated = station with { Name = stationName.Text?.Trim() ?? "", AliasAddress = alias, EnabledEntries = entries };
        var previous = project.Hardware.Stations;
        ChangeHardware(() => project.Hardware.Stations = Stations.Select(s => s.Id == station.Id ? updated : s).ToList(), () => project.Hardware.Stations = previous);
    }
    public void RemoveConfiguredStation()
    {
        var station = SelectedStation ?? throw new InvalidOperationException("请先选择工程从站。");
        if (project.Hardware.Io.Any(i => i.StationId == station.Id) || project.Hardware.Axes.Any(a => a.StationId == station.Id)) throw new InvalidOperationException("从站正被 IO 或轴引用，请先解除对应绑定。");
        var previous = project.Hardware.Stations; var remaining = Stations.Where(s => s.Id != station.Id).ToList();
        ChangeHardware(() => project.Hardware.Stations = remaining.Count == 0 ? null : remaining, () => project.Hardware.Stations = previous);
        selectedStationId = null; RefreshConfigurationViews();
    }
    public void MoveConfiguredStation(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var list = Stations.ToList(); var at = list.FindIndex(s => s.Id == selectedStationId);
        if (at < 0) throw new InvalidOperationException("请先选择工程从站。");
        var next = at + direction; if (next < 0 || next >= list.Count) return;
        (list[at], list[next]) = (list[next], list[at]); var previous = project.Hardware.Stations;
        ChangeHardware(() => project.Hardware.Stations = list, () => project.Hardware.Stations = previous);
    }
    private void RemoveCatalogDevice()
    {
        var at = deviceList.SelectedIndex; if (at < 0 || at >= project.Hardware.Devices.Count) return;
        var device = project.Hardware.Devices[at];
        if (Stations.Any(s => s.DeviceId == device.Id)) throw new InvalidOperationException("目录设备正被工程从站引用，请先删除对应从站。");
        ChangeHardware(() => project.Hardware.Devices.RemoveAt(at), () => project.Hardware.Devices.Insert(at, device));
    }
    private void SelectConfiguredStation(int index)
    {
        selectedStationId = index >= 0 && index < Stations.Count ? Stations[index].Id : null; RefreshConfigurationViews();
    }
    private void RefreshConfigurationViews()
    {
        RefreshCommunication();
        refreshingConfiguration = true;
        try
        {
            var list = Stations;
            var labels = list.Select((s, i) => $"{i + 1}. {s.Name} · 别名 {s.AliasAddress} · {s.EnabledEntries.Count} 个 PDO").ToArray();
            configuredStations.ItemsSource = labels; networkStations.ItemsSource = labels;
            var index = list.ToList().FindIndex(s => s.Id == selectedStationId);
            configuredStations.SelectedIndex = networkStations.SelectedIndex = index;
            stationPdos.Children.Clear();
            if (SelectedStation is { } selectedStation)
            {
                stationName.Text = selectedStation.Name; stationAlias.Text = selectedStation.AliasAddress.ToString(CultureInfo.InvariantCulture);
                var device = project.Hardware.Devices.Single(d => d.Id == selectedStation.DeviceId);
                foreach (var entry in device.Entries.DistinctBy(e => (e.Index,e.SubIndex,e.IsInput))) stationPdos.Children.Add(new CheckBox { Content = PdoLabel(entry), Tag = entry, IsChecked = selectedStation.EnabledEntries.Contains(entry) });
            }
            else { stationName.Text = ""; stationAlias.Text = "0"; }
            var choices = new[] { new StationChoice(null, "未绑定从站") }.Concat(list.Select(s => new StationChoice(s.Id, s.Name))).ToArray();
            var ioId = (ioStation.SelectedItem as StationChoice)?.Id; var axisId = (axisStation.SelectedItem as StationChoice)?.Id;
            ioStation.ItemsSource = choices; axisStation.ItemsSource = choices;
            ioStation.SelectedItem = choices.FirstOrDefault(c => c.Id == ioId) ?? choices[0]; axisStation.SelectedItem = choices.FirstOrDefault(c => c.Id == axisId) ?? choices[0];
            RefreshIoPdoChoices(); RefreshNetworkTopology();
        }
        finally { refreshingConfiguration = false; }
    }
    private Control HardwareIoAxisPane()
    {
        foreach (var pair in new (Control Control, string Name)[] { (ioNameEditor,"IoName"), (ioAddressEditor,"IoAddress"), (ioTypeEditor,"IoType"), (ioDirectionEditor,"IoDirection"), (ioStation,"IoStation"), (ioPdo,"IoPdo"), (axisStation,"AxisStation"), (ioList,"IoMappings"), (axisList,"AxisConfigurations"), (axisNameEditor,"AxisName"), (controllerAxisEditor,"ControllerAxis"), (axisUnitEditor,"AxisUnit"), (axisMinEditor,"AxisMinimum"), (axisMaxEditor,"AxisMaximum") }) Register(pair.Control,pair.Name);
        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(Label("IO 映射 · 加入从站时自动分配；选中表格行可在下方修改")); panel.Children.Add(IoMappingTablePane());
        panel.Children.Add(new WrapPanel { Children = { ioNameEditor, ioAddressEditor, ioTypeEditor, ioDirectionEditor, ioStation, ioPdo } });
        ioStation.SelectionChanged += (_, _) => { if (!refreshingConfiguration) RefreshIoPdoChoices(); };
        ioDirectionEditor.SelectionChanged += (_, _) => { if (!refreshingConfiguration) RefreshIoPdoChoices(); };
        ioList.SelectionChanged += (_, _) => { if (!refreshingConfiguration) LoadIoProperties(); };
        var ioActions = new WrapPanel(); AddConfigurationButton(ioActions,"添加 IO","AddConfiguredIo",()=> SaveIoProperties(false)); AddConfigurationButton(ioActions,"应用选中 IO 属性","ApplyConfiguredIo",()=> SaveIoProperties(true));
        AddConfigurationButton(ioActions,"删除选中 IO","RemoveConfiguredIo",()=> { var at=ioList.SelectedIndex; if(at<0)return; var old=project.Hardware.Io[at]; ChangeHardware(()=>project.Hardware.Io.RemoveAt(at),()=>project.Hardware.Io.Insert(at,old)); }); panel.Children.Add(ioActions);
        panel.Children.Add(Label("轴配置 · 绑定逻辑轴、控制器轴与可选从站")); panel.Children.Add(axisList);
        panel.Children.Add(new WrapPanel { Children = { axisNameEditor, controllerAxisEditor, Label("工程单位"), axisUnitEditor, Label("位置下限 / 上限"), axisMinEditor, axisMaxEditor, axisStation } });
        var limits = new WrapPanel(); foreach(var field in axisLimitEditors) limits.Children.Add(field); panel.Children.Add(limits);
        panel.Children.Add(AxisMechanicalPropertiesPane());
        axisList.SelectionChanged += (_, _) => { if (!refreshingConfiguration) LoadAxisProperties(); };
        var axisActions = new WrapPanel(); AddConfigurationButton(axisActions,"添加轴引用","AddConfiguredAxis",()=> SaveAxisProperties(false)); AddConfigurationButton(axisActions,"应用选中轴属性","ApplyConfiguredAxis",()=> SaveAxisProperties(true));
        AddConfigurationButton(axisActions,"删除选中轴","RemoveConfiguredAxis",()=> { var at=axisList.SelectedIndex; if(at<0)return; var old=project.Hardware.Axes[at]; ChangeHardware(()=>project.Hardware.Axes.RemoveAt(at),()=>project.Hardware.Axes.Insert(at,old)); }); panel.Children.Add(axisActions);
        panel.Children.Add(Label("未绑定从站的 IO / 轴引用可以保留；实际总线参数下载需控制核心执行接口，工程保存不会自动驱动设备。")); return panel;
    }
    private void RefreshIoPdoChoices()
    {
        var previous = (ioPdo.SelectedItem as PdoChoice)?.Entry;
        var station = Stations.FirstOrDefault(s => s.Id == (ioStation.SelectedItem as StationChoice)?.Id);
        var choices = station?.EnabledEntries.Where(e => e.IsInput == (ioDirectionEditor.SelectedIndex == 0)).Select(e => new PdoChoice(e)).ToArray() ?? [];
        ioPdo.ItemsSource = choices; ioPdo.SelectedItem = choices.FirstOrDefault(c => c.Entry == previous); 
    }
    private void LoadIoProperties()
    {
        var at=ioList.SelectedIndex; if(at<0||at>=project.Hardware.Io.Count)return; var io=project.Hardware.Io[at];
        ioNameEditor.Text=io.Name; ioAddressEditor.Text=io.Address; ioTypeEditor.SelectedItem=io.Type.ToUpperInvariant(); ioDirectionEditor.SelectedIndex=io.IsInput?0:1;
        ioStation.SelectedItem=((IEnumerable<StationChoice>)ioStation.ItemsSource!).FirstOrDefault(c=>c.Id==io.StationId); RefreshIoPdoChoices();
        ioPdo.SelectedItem=((IEnumerable<PdoChoice>)ioPdo.ItemsSource!).FirstOrDefault(c=>c.Entry.Index==io.PdoIndex&&c.Entry.SubIndex==io.PdoSubIndex);
    }
    public void SaveIoProperties(bool edit)
    {
        var at=ioList.SelectedIndex; if(edit&&(at<0||at>=project.Hardware.Io.Count))throw new InvalidOperationException("请先选择 IO。");
        var pdo=(ioPdo.SelectedItem as PdoChoice)?.Entry;
        var next=new IoMapping(ioNameEditor.Text?.Trim()??"",ioAddressEditor.Text?.Trim()??"",ioTypeEditor.SelectedItem?.ToString()??"",ioDirectionEditor.SelectedIndex==0) { StationId=(ioStation.SelectedItem as StationChoice)?.Id, PdoIndex=pdo?.Index, PdoSubIndex=pdo?.SubIndex };
        var old=edit?project.Hardware.Io[at]:null;
        ChangeHardware(()=> { if(edit)project.Hardware.Io[at]=next;else project.Hardware.Io.Add(next); },()=> { if(edit)project.Hardware.Io[at]=old!;else project.Hardware.Io.Remove(next); });
        ioList.SelectedIndex = edit ? at : project.Hardware.Io.Count - 1;
        LoadIoProperties();
    }
    private void LoadAxisProperties()
    {
        var at=axisList.SelectedIndex;if(at<0||at>=project.Hardware.Axes.Count)return; var axis=project.Hardware.Axes[at];
        axisNameEditor.Text=axis.Name; controllerAxisEditor.Text=axis.ControllerAxis;axisUnitEditor.Text=axis.Unit;axisMinEditor.Text=axis.MinPosition.ToString(CultureInfo.InvariantCulture);axisMaxEditor.Text=axis.MaxPosition.ToString(CultureInfo.InvariantCulture);
        var values=new[]{axis.MaximumVelocity,axis.MaximumAcceleration,axis.MaximumDeceleration,axis.MaximumJerk};for(var i=0;i<4;i++)axisLimitEditors[i].Text=values[i]?.ToString(CultureInfo.InvariantCulture)??"";
        axisStation.SelectedItem=((IEnumerable<StationChoice>)axisStation.ItemsSource!).FirstOrDefault(c=>c.Id==axis.StationId);
        LoadAxisMechanics(axis);
    }
    public void SaveAxisProperties(bool edit)
    {
        var at=axisList.SelectedIndex; if(edit&&(at<0||at>=project.Hardware.Axes.Count))throw new InvalidOperationException("请先选择轴。");
        if(!double.TryParse(axisMinEditor.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var low)||!double.TryParse(axisMaxEditor.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var high))throw new InvalidDataException("请输入有效限位数值。");
        double? Limit(TextBox field)=>string.IsNullOrWhiteSpace(field.Text)?null:double.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)&&value>0?value:throw new InvalidDataException("轴动态限制须为正数。");
        var next=new AxisConfiguration(axisNameEditor.Text?.Trim()??"",controllerAxisEditor.Text?.Trim()??"",axisUnitEditor.Text?.Trim()??"",low,high){StationId=(axisStation.SelectedItem as StationChoice)?.Id,MaximumVelocity=Limit(axisLimitEditors[0]),MaximumAcceleration=Limit(axisLimitEditors[1]),MaximumDeceleration=Limit(axisLimitEditors[2]),MaximumJerk=Limit(axisLimitEditors[3]),Mechanics=ReadAxisMechanics()};
        var old=edit?project.Hardware.Axes[at]:null;
        ChangeHardware(()=> {if(edit)project.Hardware.Axes[at]=next;else project.Hardware.Axes.Add(next);},()=> {if(edit)project.Hardware.Axes[at]=old!;else project.Hardware.Axes.Remove(next);});
        axisList.SelectedIndex = edit ? at : project.Hardware.Axes.Count - 1;
        LoadAxisProperties();
    }
}

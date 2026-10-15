using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private sealed record IoMappingTableRow(IoMapping Mapping, string Station, string Pdo)
    {
        public string Name => Mapping.Name;
        public string Address => Mapping.Address;
        public string Type => Mapping.Type;
        public string Direction => Mapping.IsInput ? "输入" : "输出";
    }
    private readonly Expander hardwareIoAxisExpander = new() { Header = "IO 映射与轴配置", IsExpanded = true };
    private Control? ioMappingTable;
    private const string IoTableColumns = "180,110,85,70,170,150";

    private Control IoMappingTablePane()
    {
        Grid Row(IReadOnlyList<string> values, bool header)
        {
            var row = new Grid { ColumnDefinitions = new(IoTableColumns), MinHeight = 32, Background = header ? Brush.Parse("#233347") : Brushes.Transparent };
            for (var index = 0; index < values.Count; index++)
            {
                var cell = new TextBlock { Text = values[index], Margin = new Thickness(7, 5), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal };
                ToolTip.SetTip(cell, values[index]); Grid.SetColumn(cell, index); row.Children.Add(cell);
            }
            return row;
        }
        var header = Row(["变量名", "地址", "类型", "方向", "从站", "PDO"], true); Register(header, "IoMappingTableHeader");
        ioList.Height = 220;
        ioList.Padding = new Thickness(0);
        ioList.Styles.Add(new Style(s => s.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        ioList.ItemTemplate = new FuncDataTemplate<IoMappingTableRow>((item, _) => item is null ? new TextBlock() : Row([item.Name, item.Address, item.Type, item.Direction, item.Station, item.Pdo], false));
        ScrollViewer.SetHorizontalScrollBarVisibility(ioList, ScrollBarVisibility.Disabled);
        var content = new StackPanel { MinWidth = 790, Children = { header, ioList } };
        ioMappingTable = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = content };
        Register(ioMappingTable, "IoMappingTable"); return ioMappingTable;
    }

    private void RefreshIoMappingTable()
    {
        var selectedMapping = (ioList.SelectedItem as IoMappingTableRow)?.Mapping;
        var rows = project.Hardware.Io.Select(io =>
        {
            var station = io.StationId is { } stationId ? Stations.FirstOrDefault(s => s.Id == stationId) : null;
            var entry = station?.EnabledEntries.FirstOrDefault(e => e.Index == io.PdoIndex && e.SubIndex == io.PdoSubIndex && e.IsInput == io.IsInput);
            return new IoMappingTableRow(io, station?.Name ?? (io.StationId is null ? "未绑定" : "从站缺失"),
                io.PdoIndex is { } index && io.PdoSubIndex is { } subIndex ? $"0x{index:X4}:{subIndex}" + (entry is null ? "" : " · " + entry.Name) : "未绑定");
        }).ToArray();
        var previousRefresh = refreshingConfiguration; refreshingConfiguration = true;
        try
        {
            ioList.ItemsSource = rows;
            ioList.SelectedIndex = Array.FindIndex(rows, row => row.Mapping == selectedMapping);
        }
        finally { refreshingConfiguration = previousRefresh; }
        if (!refreshingConfiguration && ioList.SelectedIndex >= 0) LoadIoProperties();
    }

    public void CompleteStationIoMappings()
    {
        var station = SelectedStation ?? throw new InvalidOperationException("请先选择工程从站。");
        var plan = IoAutoMapping.Plan(project.Hardware, station);
        if (plan.Added.Count > 0)
        {
            var previous = project.Hardware.Io;
            ChangeHardware(() => project.Hardware.Io = [.. previous, .. plan.Added], () => project.Hardware.Io = previous);
        }
        ReportIoMappingPlan(plan);
    }

    private void ReportIoMappingPlan(IoAutoMappingPlan plan)
    {
        var skipped = plan.Skipped.Count == 0 ? "" : "\n未自动映射：" + string.Join("；", plan.Skipped.Take(5)) + (plan.Skipped.Count > 5 ? $"；另有 {plan.Skipped.Count - 5} 项" : "");
        hardwareConfigurationStatus.Text = $"自动新增 {plan.Added.Count} 项 IO 映射；已有名称和地址保持不变。当前为离线工程配置，尚未应用到设备。" + skipped;
        hardwareConfigurationStatus.Foreground = Brush.Parse(plan.Skipped.Count > 0 ? "#FFD18A" : "#7ECED4");
    }

    private TreeViewItem IoMappingTreeNode()
    {
        var node = new TreeViewItem { Header = $"IO 映射 ({project.Hardware.Io.Count})", Tag = "io-mappings" };
        var open = new MenuItem { Header = "打开 IO 映射表格" }; open.Click += (_, _) => ShowIoMappingTable();
        node.ContextMenu = new ContextMenu { ItemsSource = new[] { open } };
        node.Tapped += (_, e) =>
        {
            if (e.Source is Control source && ReferenceEquals(source as TreeViewItem ?? source.FindAncestorOfType<TreeViewItem>(), node)) ShowIoMappingTable();
        };
        return node;
    }

    private void ShowIoMappingTable()
    {
        NavigateTo(2); hardwareIoAxisExpander.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (ioMappingTable?.IsEffectivelyVisible != true) return;
            if (this.FindControl<ScrollViewer>("HardwareConfigurationScroll") is { Content: Visual content } scroll && ioMappingTable.TranslatePoint(default, content) is { } point)
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, point.Y));
        }, DispatcherPriority.Loaded);
    }
}

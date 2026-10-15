using Avalonia.Controls;
using Avalonia.VisualTree;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private sealed record WorkbenchPageReference(int TabIndex);

    private TreeViewItem PageNode(object header, int tabIndex, ContextMenu? menu = null, IEnumerable<TreeViewItem>? children = null)
    {
        var node = new TreeViewItem { Header = header, Tag = new WorkbenchPageReference(tabIndex), ContextMenu = menu ?? NavigationMenu(header.ToString() ?? "打开", tabIndex), IsExpanded = children is not null, ItemsSource = children };
        node.Tapped += (_, e) =>
        {
            if (e.Source is Control source && ReferenceEquals(source as TreeViewItem ?? source.FindAncestorOfType<TreeViewItem>(), node)) NavigateTo(tabIndex);
        };
        return node;
    }

    private TreeViewItem TaskGroupNode() => PageNode("任务", 1, TaskGroupMenu(), project.Tasks.Select(TaskNode).ToArray());

    private TreeViewItem HardwareGroupNode() => PageNode("硬件", 2, NavigationMenu("硬件组态", 2),
    [
        PageNode($"工程从站 ({Stations.Count})", 2, NavigationMenu("硬件组态 / 从站", 2), Stations.Select((station, index) =>
            new TreeViewItem { Header = $"{index + 1}. {station.Name}", Tag = station, ContextMenu = NavigationMenu("从站属性", 2) }).ToArray()),
        IoMappingTreeNode(),
        PageNode($"轴资源 ({project.Hardware.Axes.Count})", 2, NavigationMenu("轴属性 / 配置", 2))
    ]);

    private TreeViewItem NetworkGroupNode() => PageNode("网络", 6, NavigationMenu("网络组态", 6),
    [
        PageNode(project.Network?.ManagementHost ?? "管理连接未配置", 6),
        PageNode(string.IsNullOrWhiteSpace(project.Network?.EtherCatInterface) ? "EtherCAT 网卡未配置" : project.Network.EtherCatInterface, 6)
    ]);
}

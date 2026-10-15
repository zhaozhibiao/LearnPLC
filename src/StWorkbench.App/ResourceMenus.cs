using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private MenuItem MenuAction(string title, Func<Task> action, bool enabled = true)
    {
        var menu = new MenuItem { Header = title, IsEnabled = enabled };
        menu.Click += async (_, _) => await Run(action);
        return menu;
    }
    private MenuItem AddResourceMenu(ItemKind? preferred = null) => new()
    {
        Header = preferred is null ? "添加资源" : "添加其他资源",
        ItemsSource = Enum.GetValues<ItemKind>().Select(k => MenuAction("添加" + KindLabel(k), () => AddItemAsync(k))).ToArray()
    };
    private ContextMenu CategoryMenu(ItemKind kind) => new() { ItemsSource = new object[] { MenuAction("添加" + KindLabel(kind), () => AddItemAsync(kind)), AddResourceMenu(kind) } };
    private TreeViewItem ResourceNode(ProjectItem item)
    {
        var node = new TreeViewItem { Header = item.Name, Tag = item.Id };
        if (item.Kind is ItemKind.Function or ItemKind.FunctionBlock or ItemKind.DataType) ConfigureResourceDrag(node, new(item.Id));
        if (item.Kind is ItemKind.GlobalVariables or ItemKind.DataType)
        {
            var kind = item.Kind == ItemKind.GlobalVariables ? "VAR_GLOBAL" : "TYPE";
            node.ItemsSource = language.Index([sources[item.Id]]).Where(s => s.Kind == kind).Select(s => ResourceMemberNode(item, s)).ToArray();
            node.IsExpanded = true;
        }
        node.AddHandler(PointerPressedEvent, (_, e) => { if (e.GetCurrentPoint(node).Properties.IsRightButtonPressed) { tree.SelectedItem = node; SelectItem(item.Id); } }, RoutingStrategies.Bubble, true);
        Task Target(Func<Task> action) { if (project.Items.All(i => i.Id != item.Id)) return Task.CompletedTask; SelectItem(item.Id); return action(); }
        node.ContextMenu = new ContextMenu { ItemsSource = new object[]
        {
            MenuAction("打开", () => Target(() => { NavigateTo(0); return Task.CompletedTask; })),
            MenuAction("添加" + KindLabel(item.Kind), () => AddItemAsync(item.Kind)), AddResourceMenu(item.Kind),
            new Separator(), MenuAction("复制资源…", () => DuplicateResourceAsync(item.Id), item.Kind != ItemKind.GlobalVariables),
            MenuAction("重命名…", () => Target(RenameAsync)),
            MenuAction("删除…", () => Target(DeleteAsync), !project.Tasks.Any(t => t.ProgramId == item.Id)),
            new Separator(), MenuAction("属性…", () => ResourcePropertiesAsync(item.Id))
        } };
        return node;
    }
    private void NavigateTo(int index) { if (this.FindControl<TabControl>("WorkbenchTabs") is { } tabs) tabs.SelectedIndex = index; }
    private ContextMenu NavigationMenu(string title, int index) => new() { ItemsSource = new[] { MenuAction(title, () => { NavigateTo(index); return Task.CompletedTask; }) } };
    private async Task DuplicateResourceAsync(Guid id)
    {
        if (busy) return; var item = project.Items.Single(i => i.Id == id);
        var name = await AskNameAsync("复制" + KindLabel(item.Kind), AvailableResourceName(item.Name + "Copy"));
        if (name is null || busy || !project.Items.Contains(item)) return;
        DuplicateResource(id, name);
    }
    public void DuplicateResource(Guid id, string name)
    {
        if (busy) return; ValidateName(name); var original = project.Items.Single(i => i.Id == id);
        var copy = new ProjectItem { Name = name, Kind = original.Kind, SourcePath = $"sources/{name}.st" };
        var text = sources[id].Text;
        if (original.Kind != ItemKind.GlobalVariables) text = language.RenameLocalIdentifier(text, original.Name, name);
        else throw new InvalidOperationException("全局变量不能直接复制同名声明；请添加全局变量资源并指定不同变量名。");
        project.Items.Add(copy); sources.Add(copy.Id, new(copy.Id, copy.SourcePath, text)); MarkDirty(); RefreshTree(); SelectItem(copy.Id);
    }
    private async Task ResourcePropertiesAsync(Guid id)
    {
        var item = project.Items.Single(i => i.Id == id); var source = sources[id];
        var symbols = language.Index([source]);
        var tasks = project.Tasks.Where(t => t.ProgramId == id).Select(t => t.Name).ToArray();
        var details = $"名称：{item.Name}\n类型：{KindLabel(item.Kind)}\n语言：ST\n源文件：{item.SourcePath}\n行数：{source.Text.Count(c => c == '\n') + 1}\n任务引用：{(tasks.Length == 0 ? "无" : string.Join("、", tasks))}\n\n声明接口（词法摘要）：\n" + string.Join('\n', symbols.Select(s => $"{s.Kind}  {s.Name} : {s.Type}"));
        var dialog = new Window { Title = item.Name + " · 属性", Width = 540, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => dialog.Close();
        var panel = new DockPanel { Margin = new Thickness(18) }; DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close); panel.Children.Add(new TextBox { Text = details, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap }); dialog.Content = panel;
        await dialog.ShowDialog(this);
    }
    private ContextMenu TaskGroupMenu() => new() { ItemsSource = new[] { MenuAction("添加任务…", AddTaskAsync), MenuAction("任务配置 / 属性", () => { NavigateTo(1); return Task.CompletedTask; }) } };
    private TreeViewItem TaskNode(PlcTask task) => new()
    {
        Header = $"{task.Name} · {task.IntervalMilliseconds} ms" + (task.Enabled ? "" : " · 未启用"),
        ContextMenu = new ContextMenu { ItemsSource = new object[]
        {
            MenuAction("添加任务…", AddTaskAsync), MenuAction("重命名…", () => EditTaskAsync(task)),
            MenuAction("删除…", async () => { if (busy || task.Enabled) return; if (await ConfirmAsync("删除任务", "删除 " + task.Name + "？", "删除") && !busy && !task.Enabled) { project.Tasks.Remove(task); MarkDirty(); RefreshTree(); } }, !task.Enabled),
            MenuAction("属性 / 周期配置…", () => EditTaskAsync(task))
        } }
    };
    private async Task AddTaskAsync()
    {
        if (busy) return;
        var targetProject = project;
        var name = await AskNameAsync("添加未启用任务", "NewTask"); if (name is null || busy) return;
        if (!ReferenceEquals(targetProject, project)) throw new InvalidOperationException("工程已切换，请重新添加任务。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$") || project.Tasks.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("任务名必须是有效且不重名的 ST 标识符。");
        project.Tasks.Add(new PlcTask { Name = name, ProgramId = project.Tasks.Single(t => t.Enabled).ProgramId, Enabled = false }); MarkDirty(); RefreshTree();
    }
    private async Task EditTaskAsync(PlcTask task)
    {
        if (busy || !project.Tasks.Contains(task)) return;
        var dialog = new Window { Title = "任务属性", Width = 450, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = new TextBox { Text = task.Name }; var period = new TextBox { Text = task.IntervalMilliseconds.ToString() };
        var programs = project.Items.Where(i => i.Kind == ItemKind.Program).ToArray();
        var program = new ComboBox { ItemsSource = programs.Select(i => i.Name).ToArray(), SelectedIndex = Array.FindIndex(programs, i => i.Id == task.ProgramId) };
        var message = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = task.Enabled ? "当前活动周期任务" : "未启用任务；当前 Runtime 仅支持一个启用任务。" };
        var save = new Button { Content = "应用属性" };
        save.Click += (_, _) =>
        {
            if (busy || !project.Tasks.Contains(task)) { message.Text = "工程状态已改变，请重新打开属性。"; return; }
            var candidateName = name.Text ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(candidateName, @"^[A-Za-z_][A-Za-z0-9_]*$") || project.Tasks.Any(t => !ReferenceEquals(t, task) && t.Name.Equals(candidateName, StringComparison.OrdinalIgnoreCase)) || !int.TryParse(period.Text, out var ms) || ms is < 1 or > 60000 || program.SelectedIndex < 0 || !project.Items.Contains(programs[program.SelectedIndex])) { message.Text = "请填写不重名的任务名、1–60000 ms 周期并选择程序。"; return; }
            task.Name = candidateName; task.IntervalMilliseconds = ms; task.ProgramId = programs[program.SelectedIndex].Id;
            if (task.Enabled) { taskName.Text = candidateName; taskPeriod.Text = ms.ToString(); }
            MarkDirty(); RefreshTree(); dialog.Close();
        };
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 7, Children = { Label("任务名称"), name, Label("周期（ms）"), period, program, message, save } };
        await dialog.ShowDialog(this);
    }
}

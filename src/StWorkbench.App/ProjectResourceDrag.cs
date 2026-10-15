using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private static readonly DataFormat<string> ProjectResourceFormat = DataFormat.CreateStringApplicationFormat("st-workbench-project-resource");

    private void ConfigureResourceDrag(TreeViewItem node, ProjectResourceReference reference)
    {
        ToolTip.SetTip(node, "单击打开声明；左键拖到当前代码框插入引用或调用。");
        PointerPressedEventArgs? press = null; Point origin = default; bool dragging = false;
        bool IsOwnEvent(RoutedEventArgs e)
        {
            if (e.Source is not Visual visual) return false;
            var parents = visual.GetVisualAncestors().ToArray();
            if (visual is ToggleButton || parents.TakeWhile(v => v != node).Any(v => v is ToggleButton)) return false;
            return visual == node || parents.OfType<TreeViewItem>().FirstOrDefault() == node;
        }
        node.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!IsOwnEvent(e) || !e.GetCurrentPoint(node).Properties.IsLeftButtonPressed) return;
            press = e; origin = e.GetPosition(node); e.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        node.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (press is null || !IsOwnEvent(e)) return;
            press = null; e.Handled = true;
            tree.SelectedItem = node; SelectItem(reference.ItemId); NavigateTo(0);
        }, RoutingStrategies.Tunnel, true);
        node.PointerMoved += async (_, e) =>
        {
            if (dragging || press is null || !e.GetCurrentPoint(node).Properties.IsLeftButtonPressed || Math.Abs(e.GetPosition(node).X - origin.X) + Math.Abs(e.GetPosition(node).Y - origin.Y) < 5) return;
            var start = press; press = null; dragging = true;
            try
            {
                instructionResult.Text = "拖动工程资源到当前代码框，松开鼠标插入。";
                using var data = new DataTransfer(); data.Add(DataTransferItem.Create(ProjectResourceFormat, JsonSerializer.Serialize(reference)));
                if (await DragDrop.DoDragDropAsync(start, data, DragDropEffects.Copy) == DragDropEffects.None) instructionResult.Text = "资源拖动已取消，当前源文件保持原样。";
            }
            catch (Exception error) { instructionResult.Text = "资源拖动未完成：" + error.Message; }
            finally { dragging = false; }
        };
    }

    private TreeViewItem ResourceMemberNode(ProjectItem item, StSymbol symbol)
    {
        var reference = new ProjectResourceReference(item.Id, symbol.Name);
        var node = new TreeViewItem { Header = symbol.Name + " : " + symbol.Type, Tag = reference };
        ConfigureResourceDrag(node, reference);
        return node;
    }

    private void RefreshResourceMembers(ProjectItem item)
    {
        if (item.Kind is not (ItemKind.DataType or ItemKind.GlobalVariables) || tree.ItemsSource is not IEnumerable<TreeViewItem> groups) return;
        var node = groups.SelectMany(g => g.ItemsSource as IEnumerable<TreeViewItem> ?? []).FirstOrDefault(n => n.Tag is Guid id && id == item.Id);
        if (node is null) return;
        var kind = item.Kind == ItemKind.GlobalVariables ? "VAR_GLOBAL" : "TYPE";
        var symbols = language.Index([sources[item.Id]]).Where(s => s.Kind == kind).ToArray();
        var headers = symbols.Select(s => s.Name + " : " + s.Type);
        if ((node.ItemsSource as IEnumerable<TreeViewItem> ?? []).Select(n => n.Header?.ToString()).SequenceEqual(headers)) return;
        node.ItemsSource = symbols.Select(s => ResourceMemberNode(item, s)).ToArray();
    }

    private async Task InsertProjectResourceAsync(ProjectResourceReference reference, int offset)
    {
        if (busy) throw new InvalidOperationException("当前工程操作尚未结束。");
        var item = project.Items.SingleOrDefault(i => i.Id == reference.ItemId) ?? throw new InvalidOperationException("资源已删除或工程已切换。");
        string? instance = null;
        if (item.Kind == ItemKind.FunctionBlock)
        {
            var instances = StResourceInsertion.ExistingInstances(editor.Text, offset, item.Name);
            if (instances.Count > 0)
            {
                var target = selected?.Id; var generation = editGeneration;
                var dialog = new Window { Title = "功能块实例 · " + item.Name, Width = 430, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var choices = new ComboBox { ItemsSource = new[] { "创建新实例" }.Concat(instances.Select(n => "复用 " + n)).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
                var accept = new Button { Content = "插入调用" }; var cancel = new Button { Content = "取消" };
                accept.Click += (_, _) => dialog.Close(choices.SelectedIndex); cancel.Click += (_, _) => dialog.Close(-1);
                dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children = { new TextBlock { Text = "创建独立实例，或复用当前程序中的同类型实例。", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, choices, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { accept, cancel } } } };
                var choice = await dialog.ShowDialog<int?>(this);
                if (choice is null or < 0) return;
                if (target != selected?.Id || generation != editGeneration || busy) throw new InvalidOperationException("选择期间源程序已变更，请重新拖入。");
                if (choice > 0) instance = instances[choice.Value - 1];
            }
        }
        InsertProjectResource(reference, offset, instance);
    }

    public void InsertProjectResource(ProjectResourceReference reference, int offset, string? existingInstance = null)
    {
        if (busy || selected is null) throw new InvalidOperationException("请先打开可编辑的源文件并等待工程操作结束。");
        var item = project.Items.SingleOrDefault(i => i.Id == reference.ItemId) ?? throw new InvalidOperationException("资源已删除或工程已切换。");
        var result = StResourceInsertion.Insert(editor.Text, offset, item, sources[item.Id], GetSources(), reference.Member, existingInstance);
        ShowEditorWorkspace();
        editor.Document.UndoStack.StartUndoGroup();
        try { editor.Document.Replace(0, editor.Document.TextLength, result.Text); }
        finally { editor.Document.UndoStack.EndUndoGroup(); }
        editor.Select(result.SelectionStart, result.SelectionLength); editor.ScrollToLine(editor.Document.GetLineByOffset(result.SelectionStart).LineNumber); editor.Focus(); NavigateTo(0);
        instructionResult.Text = "已插入 " + (reference.Member ?? item.Name) + "；调用参数使用占位变量，请修改为实际信号。Ctrl+Z 可一次撤销。";
    }
}

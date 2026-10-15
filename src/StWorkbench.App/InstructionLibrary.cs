using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private static readonly DataFormat<string> InstructionFormat = DataFormat.CreateStringApplicationFormat("st-workbench-instruction-id");
    private readonly TreeView instructionTree = new();
    private readonly TextBox instructionSearch = new() { PlaceholderText = "搜索名称、用途、指令…" };
    private readonly TextBlock axisInstructionHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A9BAD1") };
    private readonly TextBlock instructionDescription = new() { Text = "左键拖到代码位置，或双击 / 点击插入。实例声明会自动添加；Ctrl+Z 撤销。", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A9BAD1") };
    private readonly TextBox instructionPreview = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas, Microsoft YaHei"), FontSize = 12, Height = 145, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock instructionResult = new() { TextWrapping = TextWrapping.Wrap, MaxLines=2, TextTrimming=TextTrimming.CharacterEllipsis, Foreground = Brush.Parse("#7ECED4") };

    private Control InstructionLibraryPane()
    {
        Register(instructionTree, "InstructionLibrary"); Register(instructionSearch, "InstructionSearch"); Register(instructionResult, "InstructionResult");
        Register(axisInstructionHint, "AxisInstructionHint");
        var panel = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "指令与资源库", FontSize = 18, FontWeight = FontWeight.SemiBold }, instructionSearch, axisInstructionHint, ActionButton("配置工程轴", () => { if (this.FindControl<TabControl>("WorkbenchTabs") is { } tabs) tabs.SelectedIndex = 2; return Task.CompletedTask; }) } };
        DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        // Keep the list visible in short windows. The variable-length description
        // and code preview belong in a popup, not in the list's remaining height.
        var previewButton=new Button { Content="说明 / 代码预览", Padding=new Thickness(12,7), Flyout=new Flyout { Content=new StackPanel { Width=330, Spacing=7, Children={instructionDescription,instructionPreview} } } };
        Register(previewButton,"InstructionPreviewButton");
        var actions=new WrapPanel { Children={previewButton,ActionButton("插入到当前光标", () => { InsertSelectedInstruction(); return Task.CompletedTask; })} };
        var bottom = new StackPanel { Spacing = 7, Children = { actions, instructionResult } };
        DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom); panel.Children.Add(instructionTree);
        instructionSearch.TextChanged += (_, _) => RefreshInstructionLibrary();
        instructionTree.SelectionChanged += (_, _) =>
        {
            if (instructionTree.SelectedItem is not TreeViewItem { Tag: StInstruction instruction }) return;
            instructionDescription.Text = instruction.Description;
            instructionPreview.Text = string.Join("\n", instruction.Declarations) + (instruction.Declarations.Length > 0 ? "\n\n" : "") + instruction.Code;
            instructionPreview.Text = instructionPreview.Text.Replace("{p}", instruction.InstanceNameHint is { } name ? name + "_1" : "My" + instruction.Id);
        };
        instructionTree.DoubleTapped += (_, e) => { InsertSelectedInstruction(); e.Handled = true; };
        RefreshInstructionLibrary();
        return new Border { Background = Brush.Parse("#101F30"), CornerRadius = new CornerRadius(6), Child = panel, MinWidth = 220 };
    }
    private void RefreshInstructionLibrary()
    {
        axisInstructionHint.Text = project.Hardware.Axes.Count == 0 ? "请先配置工程轴，再从“工程轴”拖入真实调用。" : $"已配置 {project.Hardware.Axes.Count} 个工程轴；下方“工程轴”条目可拖入 ST。";
        var query = instructionSearch.Text?.Trim() ?? "";
        var matches = AvailableInstructions().Where(s => (s.Id + " " + s.Name + " " + s.Category + " " + s.Description).Contains(query, StringComparison.OrdinalIgnoreCase));
        instructionTree.ItemsSource = matches.GroupBy(s => s.Category).Select(g => new TreeViewItem { Header = g.Key, IsExpanded = query.Length > 0 || g.Key is "工程路径" or "工程轴" or "逻辑与门控" or "流程与程序段" or "计时器" or "计数器", ItemsSource = g.Select(InstructionNode).ToArray() }).ToArray();
    }
    private TreeViewItem InstructionNode(StInstruction instruction)
    {
        var node = new TreeViewItem { Header = instruction.ToString(), Tag = instruction };
        ToolTip.SetTip(node, instruction.Description + "\n左键拖入代码，或双击插入。");
        PointerPressedEventArgs? press = null; Point origin = default; bool dragging = false;
        // TreeView handles selection first; capture the press even when that routed event is handled.
        node.AddHandler(PointerPressedEvent, (_, e) => { if (e.GetCurrentPoint(node).Properties.IsLeftButtonPressed) { press = e; origin = e.GetPosition(node); } }, RoutingStrategies.Bubble, true);
        node.PointerReleased += (_, _) => press = null;
        node.PointerMoved += async (_, e) =>
        {
            if (dragging || press is null || !e.GetCurrentPoint(node).Properties.IsLeftButtonPressed || Math.Abs(e.GetPosition(node).X - origin.X) + Math.Abs(e.GetPosition(node).Y - origin.Y) < 5) return;
            var start = press; press = null; dragging = true;
            try
            {
                instructionResult.Text = "拖动到代码位置后松开鼠标插入。";
                using var data = new DataTransfer(); data.Add(DataTransferItem.Create(InstructionFormat, instruction.Id));
                if (await DragDrop.DoDragDropAsync(start, data, DragDropEffects.Copy) == DragDropEffects.None) instructionResult.Text = "拖动已取消；也可双击或点击插入。";
            }
            catch (Exception error) { instructionResult.Text = "拖动未完成：" + error.Message; }
            finally { dragging = false; }
        };
        return node;
    }
    private void InsertSelectedInstruction()
    {
        if (instructionTree.SelectedItem is TreeViewItem { Tag: StInstruction instruction }) TryInsertInstruction(instruction.Id, editor.CaretOffset);
        else instructionResult.Text = "请先选择一条指令。";
    }
    private void SetupInstructionDrop()
    {
        DragDrop.SetAllowDrop(editor, true); DragDrop.SetAllowDrop(editor.TextArea, true);
        editor.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(ProjectResourceFormat))
            {
                e.DragEffects = selected is not null && !busy ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true; return;
            }
            if (!e.DataTransfer.Contains(InstructionFormat)) return;
            e.DragEffects = selected?.Kind is ItemKind.Program or ItemKind.Function or ItemKind.FunctionBlock ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }, RoutingStrategies.Bubble, true);
        editor.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var resourceJson = e.DataTransfer.TryGetValue(ProjectResourceFormat);
            if (resourceJson is not null)
            {
                e.Handled = true;
                try
                {
                    if (resourceJson.Length > 4096) throw new InvalidOperationException("资源拖放数据过长。");
                    var resource = System.Text.Json.JsonSerializer.Deserialize<ProjectResourceReference>(resourceJson) ?? throw new InvalidOperationException("资源拖放数据无效。");
                    var at = editor.GetPositionFromPoint(e.GetPosition(editor));
                    if (at is null) throw new InvalidOperationException("请拖到代码文字区域。");
                    await InsertProjectResourceAsync(resource, editor.Document.GetOffset(at.Value.Line, at.Value.Column));
                }
                catch (Exception error) { instructionResult.Text = "未插入：" + error.Message; }
                return;
            }
            var id = e.DataTransfer.TryGetValue(InstructionFormat); if (id is null) return;
            e.Handled = true;
            var position = editor.GetPositionFromPoint(e.GetPosition(editor));
            if (position is null) { instructionResult.Text = "请拖到代码文字区域。"; return; }
            TryInsertInstruction(id, editor.Document.GetOffset(position.Value.Line, position.Value.Column));
        }, RoutingStrategies.Bubble, true);
    }
    private void TryInsertInstruction(string id, int offset)
    {
        try { InsertInstruction(id, offset); }
        catch (Exception error) { instructionResult.Text = "未插入：" + error.Message; }
    }
    public void InsertInstruction(string id, int offset)
    {
        if (selected is null) throw new InvalidOperationException("请先打开一个 ST 程序。");
        var instruction = AvailableInstructions().SingleOrDefault(s => s.Id == id);
        if (instruction is null)
            throw new InvalidOperationException(AxisControlBinding.BlockNames.Contains(id, StringComparer.OrdinalIgnoreCase) || id.StartsWith("Axis_", StringComparison.Ordinal)
                ? "请先配置工程轴，并从资源库的“工程轴”分类选择对应轴的指令。"
                : id == "EC_ExecutePath" ? "请先导入路径，并从“工程路径”选择对应的预存路径。" : "工程资源已改变，请重新选择资源库中的指令。");
        ShowEditorWorkspace();
        var result = StSnippetInsertion.Insert(editor.Text, offset, instruction);
        editor.Document.UndoStack.StartUndoGroup();
        try { editor.Document.Replace(0, editor.Document.TextLength, result.Text); }
        finally { editor.Document.UndoStack.EndUndoGroup(); }
        editor.Select(result.SelectionStart, result.SelectionLength); editor.ScrollToLine(editor.Document.GetLineByOffset(result.SelectionStart).LineNumber); editor.Focus();
        if (this.FindControl<TabControl>("WorkbenchTabs") is { } tabs) tabs.SelectedIndex = 0;
        instructionResult.Text = instruction.ReferenceOnly ? "已插入接口注释示例；轴控桥接完成前不会执行。" : "已插入 " + instruction.Name + "；请修改为实际信号，Ctrl+Z 可一次撤销。";
    }
}

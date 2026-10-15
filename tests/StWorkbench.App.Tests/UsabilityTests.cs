using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using StWorkbench.App;
using StWorkbench.Core;

public static class UsabilityTests
{
    public static void Run(MainWindow window)
    {
        var editor = window.FindControl<TextEditor>("SourceEditor")!;
        var tree = window.FindControl<TreeView>("ProjectTree")!;
        var groups = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).ToArray();
        var main = ((IEnumerable<TreeViewItem>)groups[0].ItemsSource!).Single();
        var menus = ((IEnumerable<object>)main.ContextMenu!.ItemsSource!).OfType<MenuItem>().ToArray();
        if (!menus.Any(m => m.Header?.ToString() == "属性…") || !menus.Any(m => m.Header?.ToString() == "删除…" && !m.IsEnabled) || groups.Any(g => g.ContextMenu is null)) throw new Exception("FAIL: resource menus, properties and referenced-program deletion guard");
        var id = (Guid)main.Tag!;
        window.DuplicateResource(id, "MainCopy");
        if (!editor.Text.Contains("PROGRAM MainCopy") || !editor.Text.Contains("Count := Count")) throw new Exception("FAIL: resource duplication preserves source and changes POU name");
        groups = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).ToArray(); main = ((IEnumerable<TreeViewItem>)groups[0].ItemsSource!).First();
        Dispatcher.UIThread.RunJobs();
        var point = main.TranslatePoint(new Point(35, 12), window)!.Value;
        window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Dispatcher.UIThread.RunJobs();
        if (!editor.Text.Contains("PROGRAM Main\n") || tree.SelectedItem != main) throw new Exception("FAIL: right click must target clicked resource rather than previous selection");
        using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/resource-context-menu.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        main.ContextMenu!.Close();
        var source = editor.Text;
        window.InsertInstruction("TON", source.IndexOf("END_PROGRAM", StringComparison.Ordinal));
        if (!editor.Text.Contains("SnippetTON1 : TON;") || !window.FindControl<TextBlock>("ProjectStatus")!.Text!.Contains("未保存")) throw new Exception("FAIL: library insertion and dirty marking");
        var caretBeforeMenu = editor.CaretOffset;
        window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Dispatcher.UIThread.RunJobs(); main.ContextMenu!.Close();
        if (editor.CaretOffset != caretBeforeMenu) throw new Exception("FAIL: right-click current resource reset editor caret");
        editor.Document.UndoStack.Undo();
        if (editor.Text != source) throw new Exception("FAIL: instance and invocation must undo together");
        var search = window.FindControl<TextBox>("InstructionSearch")!; search.Text = "TON"; Dispatcher.UIThread.RunJobs();
        var library = window.FindControl<TreeView>("InstructionLibrary")!;
        var match = ((IEnumerable<TreeViewItem>)library.ItemsSource!).SelectMany(g => (IEnumerable<TreeViewItem>)g.ItemsSource!).ToArray();
        if (match.Length != 1 || match[0].Tag is not StInstruction { Id: "TON" }) throw new Exception("FAIL: searchable categorized instruction library");
        library.SelectedItem = match[0];
        var gesturePoint = match[0].TranslatePoint(new Point(90, 12), window)!.Value;
        window.MouseDown(gesturePoint, MouseButton.Left); window.MouseMove(gesturePoint + new Point(12, 0), RawInputModifiers.LeftMouseButton); window.MouseUp(gesturePoint + new Point(12, 0), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        if (!window.FindControl<TextBlock>("InstructionResult")!.Text!.Contains("拖动")) throw new Exception("FAIL: tree selection consumed left press and prevented drag gesture");
        // Exercise the same typed drag event and routed editor drop handler as desktop drag-and-drop.
        editor.CaretOffset = source.IndexOf("END_PROGRAM", StringComparison.Ordinal);
        editor.TextArea.TextView.EnsureVisualLines();
        var position = editor.TextArea.TextView.GetVisualPosition(editor.TextArea.Caret.Position, AvaloniaEdit.Rendering.VisualYPosition.LineMiddle) - editor.TextArea.TextView.ScrollOffset;
        var dropPoint = editor.TextArea.TextView.TranslatePoint(position, editor)!.Value;
        using var data = new DataTransfer(); data.Add(DataTransferItem.Create((DataFormat<string>)typeof(MainWindow).GetField("InstructionFormat", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!, "TON"));
        var drag = new DragEventArgs(DragDrop.DragOverEvent, data, editor, dropPoint, KeyModifiers.None) { DragEffects = DragDropEffects.Copy };
        editor.RaiseEvent(drag); if (!drag.Handled || drag.DragEffects != DragDropEffects.Copy) throw new Exception("FAIL: typed instruction drag not accepted");
        var drop = new DragEventArgs(DragDrop.DropEvent, data, editor, dropPoint, KeyModifiers.None); editor.RaiseEvent(drop);
        if (!drop.Handled || !editor.Text.Contains("SnippetTON1(IN :=")) throw new Exception("FAIL: drop did not insert at editor position");
        Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/instruction-library.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        editor.Document.UndoStack.Undo(); search.Text = "";
        Console.WriteLine("PASS resource context targeting, duplicate, bound-program guard, instruction search, routed drag/drop, auto instance declarations and single undo");
    }
}

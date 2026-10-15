using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using StWorkbench.App;
using StWorkbench.Core;

public static class ResourceDragTests
{
    public static void Run()
    {
        var main = new ProjectItem();
        var function = new ProjectItem { Name = "AddTwo", Kind = ItemKind.Function, SourcePath = "sources/AddTwo.st" };
        var block = new ProjectItem { Name = "Gate", Kind = ItemKind.FunctionBlock, SourcePath = "sources/Gate.st" };
        var globals = new ProjectItem { Name = "Globals", Kind = ItemKind.GlobalVariables, SourcePath = "sources/Globals.st" };
        var types = new ProjectItem { Name = "Settings", Kind = ItemKind.DataType, SourcePath = "sources/Settings.st" };
        var project = new StProject { Name = "Resource drag verification", Items = [main, function, block, globals, types], Tasks = [new PlcTask { ProgramId = main.Id }] };
        SourceDocument[] sources = [
            new(main.Id, main.SourcePath, "PROGRAM Main\nVAR\n    ExistingGate : Gate;\n    Value : INT;\nEND_VAR\n\nValue := 0;\nEND_PROGRAM\n"),
            new(function.Id, function.SourcePath, "FUNCTION AddTwo : INT\nVAR_INPUT\n    A, B, Result : INT;\nEND_VAR\nAddTwo := A + B + Result;\nEND_FUNCTION\n"),
            new(block.Id, block.SourcePath, "FUNCTION_BLOCK Gate\nVAR_INPUT\n    Enable : BOOL;\n    Values : ARRAY[0..2] OF INT;\n    Label : STRING;\nEND_VAR\nVAR_IN_OUT\n    Counter : INT;\nEND_VAR\nVAR_OUTPUT\n    Done : BOOL;\nEND_VAR\nDone := Enable;\nEND_FUNCTION_BLOCK\n"),
            new(globals.Id, globals.SourcePath, "VAR_GLOBAL\n    GlobalValue : INT := 4;\nEND_VAR\n"),
            new(types.Id, types.SourcePath, "TYPE Settings : STRUCT\n    Count : INT;\nEND_STRUCT;\nEND_TYPE\n")];
        var path = Path.GetFullPath("artifacts/resource-ui-tests/ResourceDrag.stproj");
        Pump(new ProjectStore().SaveAsync(path, project, sources));
        var window = new MainWindow(); window.Show(); Pump(window.LoadAtAsync(path));
        try
        {
            var editor = window.FindControl<TextEditor>("SourceEditor")!;
            var tree = window.FindControl<TreeView>("ProjectTree")!;
            var groups = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).ToArray();
            var functionNode = ((IEnumerable<TreeViewItem>)groups[1].ItemsSource!).Single();
            Dispatcher.UIThread.RunJobs();
            var point = functionNode.TranslatePoint(new Point(70, 12), window)!.Value;
            var before = editor.Text;
            window.MouseDown(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            if (editor.Text != before) throw new Exception("FAIL: resource press changed target source before dragging");
            window.MouseMove(point + new Point(12, 0), RawInputModifiers.LeftMouseButton); window.MouseUp(point + new Point(12, 0), MouseButton.Left); Dispatcher.UIThread.RunJobs();
            if (editor.Text != before) throw new Exception("FAIL: resource drag switched target source");
            if (!window.FindControl<TextBlock>("InstructionResult")!.Text!.Contains("拖动")) throw new Exception("FAIL: missing resource drag gesture");
            Drop(window, new(function.Id), editor.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal));
            if (!editor.Text.Contains("AddTwo(A :=")) throw new Exception("FAIL: routed project function drop");
            editor.Document.UndoStack.Undo(); if (editor.Text != before) throw new Exception("FAIL: function declaration and call must undo together");
            var insert = typeof(MainWindow).GetMethod("InsertProjectResource") ?? throw new Exception("FAIL: resource insertion action missing");
            insert.Invoke(window, [new ProjectResourceReference(block.Id), editor.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal), "ExistingGate"]);
            if (!editor.Text.Contains("ExistingGate(Enable :=")) throw new Exception("FAIL: explicit FB reuse");
            editor.Document.UndoStack.Undo(); if (editor.Text != before) throw new Exception("FAIL: FB insertion must undo together");
            var globalNode = ((IEnumerable<TreeViewItem>)groups[4].ItemsSource!).Single();
            if (((IEnumerable<TreeViewItem>)globalNode.ItemsSource!).Single().Tag is not ProjectResourceReference { Member: "GlobalValue" }) throw new Exception("FAIL: global members not exposed");
            Drop(window, new(types.Id, "Settings"), editor.Text.IndexOf("ExistingGate", StringComparison.Ordinal));
            Drop(window, new(function.Id), editor.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal));
            Drop(window, new(block.Id), editor.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal), invokeDirect: true);
            var zero = editor.Text.IndexOf("0;", StringComparison.Ordinal);
            editor.Document.Remove(zero, 1); Drop(window, new(globals.Id, "GlobalValue"), zero);
            if (!editor.Text.Contains("Value := GlobalValue;")) throw new Exception("FAIL: global reference insertion");
            if (Environment.GetEnvironmentVariable("ST_RESOURCE_COMPILE_VERIFY") == "1")
            {
                Pump(window.GeneratePackageAsync());
                if (!window.FindControl<Button>("DownloadButton")!.IsEnabled)
                    throw new Exception("FAIL: generated project resources did not compile: " + ((TextBox)typeof(MainWindow).GetField("log", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!).Text);
                Console.WriteLine("PASS real ST compiler: drag-generated functions, Result input, FB instances, arrays, STRING, IN_OUT, globals and user datatype candidate");
            }
            using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/st-workbench-resource-drag.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            // A click still opens the definition once the pointer is released.
            Dispatcher.UIThread.RunJobs(); point = functionNode.TranslatePoint(new Point(70, 12), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            if (!editor.Text.Contains("FUNCTION AddTwo")) throw new Exception("FAIL: ordinary resource click no longer opens source");
            tree.SelectedItem = globalNode; Dispatcher.UIThread.RunJobs();
            editor.Text = editor.Text.Replace("END_VAR", "    AddedValue : BOOL;\nEND_VAR", StringComparison.Ordinal); Dispatcher.UIThread.RunJobs();
            if (!((IEnumerable<TreeViewItem>)globalNode.ItemsSource!).Any(n => n.Tag is ProjectResourceReference { Member: "AddedValue" })) throw new Exception("FAIL: edited global declaration did not update resource members");
            Console.WriteLine("PASS project-tree drag preserves target, routed calls/globals/types, instance reuse, grouped undo and normal click");
        }
        finally { window.Close(); }
    }
    private static void Drop(MainWindow window, ProjectResourceReference resource, int offset, bool invokeDirect = false)
    {
        if (invokeDirect) { typeof(MainWindow).GetMethod("InsertProjectResource")!.Invoke(window, [resource, offset, null]); return; }
        var editor = window.FindControl<TextEditor>("SourceEditor")!;
        editor.CaretOffset = offset; editor.TextArea.TextView.EnsureVisualLines();
        var position = editor.TextArea.TextView.GetVisualPosition(editor.TextArea.Caret.Position, AvaloniaEdit.Rendering.VisualYPosition.LineMiddle) - editor.TextArea.TextView.ScrollOffset;
        var point = editor.TextArea.TextView.TranslatePoint(position, editor)!.Value;
        using var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DataFormat.CreateStringApplicationFormat("st-workbench-project-resource"), System.Text.Json.JsonSerializer.Serialize(resource)));
        var args = new DragEventArgs(DragDrop.DropEvent, data, editor, point, KeyModifiers.None);
        editor.RaiseEvent(args); Dispatcher.UIThread.RunJobs();
        if (!args.Handled) throw new Exception("FAIL: project resource drop not handled");
    }
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Resource UI operation timed out"); task.GetAwaiter().GetResult();
    }
}

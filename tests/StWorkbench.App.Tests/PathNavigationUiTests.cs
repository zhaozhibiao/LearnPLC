using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using StWorkbench.App;

internal static class PathNavigationUiTests
{
    public static void Run()
    {
        var window = new MainWindow(); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var tree = window.FindControl<TreeView>("ProjectTree")!;
            var tabs = window.FindControl<TabControl>("WorkbenchTabs")!;
            var group = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).Single(n => Equals(n.Header, "路径资源"));
            var source = window.FindControl<TextEditor>("SourceEditor")!.Text;
            void ClickGroup()
            {
                var point = group.TranslatePoint(new Point(80, 12), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            }
            void AssertWorkspace()
            {
                if (tabs.SelectedIndex != 0 || !window.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "导入 DXF")))
                    throw new Exception("FAIL: clicking path resource group must open the DXF import workspace");
            }
            tabs.SelectedIndex = 2; ClickGroup(); AssertWorkspace();
            window.FindControl<TextBox>("PathName")!.Text = "UnsavedContour";
            tabs.SelectedIndex = 6; ClickGroup(); AssertWorkspace();
            if (window.FindControl<TextBox>("PathName")!.Text != "UnsavedContour") throw new Exception("FAIL: reopening path group erased draft");
            tree.SelectedItem = null; tree.SelectedItem = group; Dispatcher.UIThread.RunJobs(); AssertWorkspace();
            if (window.FindControl<TextEditor>("SourceEditor")!.Text != source) throw new Exception("FAIL: path navigation changed ST source");
            Console.WriteLine("PASS path group left click, repeated navigation, keyboard selection and draft preservation");
        }
        finally { window.Close(); }
    }
}

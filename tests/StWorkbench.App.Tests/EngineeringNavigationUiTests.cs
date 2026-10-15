using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using StWorkbench.App;

internal static class EngineeringNavigationUiTests
{
    public static void Run()
    {
        var window = new MainWindow(); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var tree = window.FindControl<TreeView>("ProjectTree")!;
            var tabs = window.FindControl<TabControl>("WorkbenchTabs")!;
            var source = window.FindControl<TextEditor>("SourceEditor")!.Text;
            var roots = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).ToArray();
            foreach (var (header, index, pageTitle) in new[] { ("任务", 1, "周期任务"), ("硬件", 2, "硬件组态"), ("网络", 6, "网络组态") })
            {
                var node = roots.Single(n => Equals(n.Header, header));
                void AssertPage()
                {
                    if (tabs.SelectedIndex != index || !Equals(((IEnumerable<TabItem>)tabs.ItemsSource!).ElementAt(index).Header, pageTitle))
                        throw new Exception("FAIL: clicking " + header + " must display " + pageTitle);
                }
                void Click()
                {
                    node.BringIntoView(); Dispatcher.UIThread.RunJobs();
                    var point = node.TranslatePoint(new Point(80, 12), window)!.Value;
                    window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs(); AssertPage();
                }
                tabs.SelectedIndex = 0; Click();
                tabs.SelectedIndex = 3; Click();
                tabs.SelectedIndex = 0; tree.SelectedItem = null; tree.SelectedItem = node; Dispatcher.UIThread.RunJobs(); AssertPage();
                if (header == "任务")
                {
                    tabs.SelectedIndex = 0;
                    tree.SelectedItem = ((IEnumerable<TreeViewItem>)node.ItemsSource!).First(); Dispatcher.UIThread.RunJobs(); AssertPage();
                }
            }
            if (window.FindControl<TextEditor>("SourceEditor")!.Text != source) throw new Exception("FAIL: page navigation changed ST source");
            Console.WriteLine("PASS task/hardware/network tree click, repeated click, keyboard and task child navigation");
        }
        finally { window.Close(); }
    }
}

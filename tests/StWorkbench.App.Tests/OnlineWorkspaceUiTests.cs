using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StWorkbench.App;
using StWorkbench.OpenPlc;
using System.Reflection;

internal static class OnlineWorkspaceUiTests
{
    public static void Run()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var tabs = window.FindControl<TabControl>("WorkbenchTabs")!;
            tabs.SelectedIndex = 5; Dispatcher.UIThread.RunJobs();
            var page = (Control)((TabItem[])tabs.ItemsSource!)[5].Content!;
            var buttons = page.GetVisualDescendants().OfType<Button>().ToArray();
            if (!buttons.Any(b => Equals(b.Content,"开始监视")) || !buttons.Any(b => Equals(b.Content,"断开监视")))
                throw new Exception("FAIL concise monitor actions missing");
            if (buttons.Any(b => Equals(b.Content,"读取设备状态 / 日志")))
                throw new Exception("FAIL raw device diagnostics still dominate monitor workspace");
            if (window.FindControl<TreeView>("InstanceTree")!.Bounds.Height < 280)
                throw new Exception("FAIL variables workspace too short");
            var diagnostics = window.FindControl<Expander>("RuntimeDiagnostics");
            if (diagnostics is null || diagnostics.IsExpanded) throw new Exception("FAIL diagnostics should be available but collapsed");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetField("debugSymbols",flags)!.SetValue(window,new DebugSymbol[] {new("INSTANCE0.VALUE","INT",new(0,1,2))});
            void Refresh() { typeof(MainWindow).GetMethod("RefreshInstanceTree",flags)!.Invoke(window,null); Dispatcher.UIThread.RunJobs(); }
            Refresh();
            var tree = window.FindControl<TreeView>("InstanceTree")!;
            var root = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).Single();
            tree.SelectedItem = ((IEnumerable<TreeViewItem>)root.ItemsSource!).Single(); root.IsExpanded = false;
            Refresh();
            if (((TreeViewItem)tree.SelectedItem!).Tag as string != "INSTANCE0.VALUE" || ((IEnumerable<TreeViewItem>)tree.ItemsSource!).Single().IsExpanded)
                throw new Exception("FAIL sampling reset selected variable or collapsed instance");
            window.Width = 1080; window.Height = 720; Dispatcher.UIThread.RunJobs();
            var write = window.FindControl<Expander>("OnlineWriteExpander")!;
            write.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            var symbol = window.FindControl<ComboBox>("OnlineWriteSymbol")!;
            var action = window.FindControl<Button>("OnlineWriteOnce")!;
            if (symbol.Parent is not WrapPanel wrap || action.Bounds.Width < 20 || action.Bounds.Right > wrap.Bounds.Width + 1)
                throw new Exception("FAIL write controls overflow narrow workspace");
            action.BringIntoView(); Dispatcher.UIThread.RunJobs();
            var position = action.TranslatePoint(default,window)!.Value;
            if(position.Y < 0 || position.Y + action.Bounds.Height > window.Bounds.Height) throw new Exception("FAIL write action inaccessible at small window size");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/online-workspace-write-small.png",Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            write.IsExpanded = false; Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/online-workspace-small.png",Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            window.Width = 1440; window.Height = 940; Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/online-workspace.png",Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Console.WriteLine("PASS simplified monitor actions, larger variables/trend workspace, collapsed diagnostics and narrow write layout");
        }
        finally { typeof(MainWindow).GetField("allowClose",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,true); window.Close(); }
    }
}

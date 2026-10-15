using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StWorkbench.App;
using StWorkbench.Core;

internal static class IoMappingUiTests
{
    public static void Run()
    {
        var window = new MainWindow(); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            T Control<T>(string name) where T : Control => window.FindControl<T>(name) ?? throw new Exception("FAIL: missing IO table control " + name);
            var project = (StProject)Field("project").GetValue(window)!;
            var digital = new PdoEntry(0x6000, 1, 1, "Ready", "BOOL", true);
            var analog = new PdoEntry(0x7000, 1, 32, "Target", "DINT", false);
            project.Hardware.Devices.Add(new(Guid.NewGuid(), "IO device", 1, 2, 3, [digital, analog]));
            Method("RefreshHardware").Invoke(window, null);
            Control<ListBox>("DeviceCatalog").SelectedIndex = 0;
            Control<TextBox>("StationName").Text = "RemoteIO"; window.AddConfiguredStation();
            if (project.Hardware.Io.Count != 2) throw new Exception("FAIL: adding station must auto-create enabled PDO IO mappings");
            var first = project.Hardware.Io.ToArray();
            Control<TextBox>("StationName").Text = "RemoteIO2"; window.AddConfiguredStation();
            if (project.Hardware.Io.Count != 4 || !project.Hardware.Io.Take(2).SequenceEqual(first)) throw new Exception("FAIL: second station reallocates existing mappings");
            var mappings = Control<ListBox>("IoMappings");
            if (mappings.ItemCount != 4) throw new Exception("FAIL: IO table row count differs from project");
            var headers = Control<Control>("IoMappingTableHeader").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            foreach (var label in new[] { "变量名", "地址", "类型", "方向", "从站", "PDO" }) if (!headers.Contains(label)) throw new Exception("FAIL: missing IO column " + label);
            Method("CompleteStationIoMappings").Invoke(window, null);
            if (project.Hardware.Io.Count != 4) throw new Exception("FAIL: IO completion is not idempotent");
            var prior = project.Hardware.Stations!.Count;
            Control<TextBox>("StationName").Text = "RemoteIO";
            try { window.AddConfiguredStation(); throw new Exception("FAIL: duplicate station accepted"); } catch (InvalidDataException) { }
            if (project.Hardware.Stations.Count != prior || project.Hardware.Io.Count != 4) throw new Exception("FAIL: failed auto-add did not roll back station and IO together");
            Dispatcher.UIThread.RunJobs();
            var tree = Control<TreeView>("ProjectTree"); var tabs = Control<TabControl>("WorkbenchTabs");
            var hardware = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).Single(n => Equals(n.Header, "硬件"));
            var node = ((IEnumerable<TreeViewItem>)hardware.ItemsSource!).Single(n => n.Header!.ToString()!.StartsWith("IO 映射"));
            void ClickIo()
            {
                var point = node.TranslatePoint(new Point(80, 12), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                if (tabs.SelectedIndex != 2 || !Control<Expander>("HardwareIoAxisExpander").IsExpanded) throw new Exception("FAIL: IO tree navigation must open hardware mapping table");
                var header = Control<Control>("IoMappingTableHeader");
                var scroll = Control<ScrollViewer>("HardwareConfigurationScroll");
                var position = header.TranslatePoint(default, scroll)!.Value;
                if (position.Y < -1 || position.Y >= scroll.Bounds.Height) throw new Exception("FAIL: IO navigation leaves table outside viewport");
            }
            Control<Expander>("HardwareIoAxisExpander").IsExpanded = false;
            tabs.SelectedIndex = 6; ClickIo();
            tabs.SelectedIndex = 0; ClickIo();
            tabs.SelectedIndex = 6; tree.SelectedItem = null; tree.SelectedItem = node; Dispatcher.UIThread.RunJobs();
            if (tabs.SelectedIndex != 2) throw new Exception("FAIL: keyboard IO selection must open mapping table");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save("artifacts/io-mapping-table.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            mappings.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Control<ListBox>("ConfiguredStations").SelectedIndex = 1;
            Control<TextBox>("StationName").Text = "RenamedStation"; window.ApplyStationProperties(); Dispatcher.UIThread.RunJobs();
            if (mappings.SelectedIndex != 2 || Control<TextBox>("IoName").Text != project.Hardware.Io[2].Name) throw new Exception("FAIL: station refresh lost selected IO identity");
            Control<TextBox>("IoName").Text = "DI_Renamed"; window.SaveIoProperties(true);
            if (project.Hardware.Io[2].Name != "DI_Renamed" || project.Hardware.Io[0] != first[0]) throw new Exception("FAIL: table edit changed wrong IO row");
            var file = Path.GetFullPath("artifacts/io-mapping-ui/AutoIo.stproj");
            Pump(window.SaveAtAsync(file)); Pump(window.LoadAtAsync(file));
            project = (StProject)Field("project").GetValue(window)!;
            if (project.Hardware.Io.Count != 4 || project.Hardware.Io[2].Name != "DI_Renamed" || project.Hardware.Io[0].Address != first[0].Address) throw new Exception("FAIL: automatic mappings did not survive save/reopen");
            Console.WriteLine("PASS automatic station IO allocation, table columns, navigation, atomic rollback, row edit and persistence");
        }
        finally { window.Close(); }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static void Pump(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult();
    }
}

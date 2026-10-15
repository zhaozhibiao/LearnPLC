using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.OpenPlc;

public static class ConfigurationUiTests
{
    public static void Run()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var tabs = window.FindControl<TabControl>("WorkbenchTabs")!;
            var headers = ((IEnumerable<TabItem>)tabs.ItemsSource!).Select(t => t.Header?.ToString()).ToArray();
            if (!headers.Contains("硬件组态") || !headers.Contains("网络组态")) throw new Exception("FAIL: independent hardware/network configuration pages missing");
            if (window.FindControl<ListBox>("ConfiguredStations") is null || window.FindControl<ListBox>("NetworkStations") is null) throw new Exception("FAIL: catalog and configured stations must be separate");
            var project=(StProject)Field("project").GetValue(window)!;
            var input=new PdoEntry(0x6000,1,1,"Ready","BOOL",true);
            project.Hardware.Devices.Add(new(Guid.NewGuid(),"Test servo",1,2,3,[input,new(0x7000,1,32,"Target","DINT",false)]));
            Method("RefreshHardware").Invoke(window,null);
            window.FindControl<ListBox>("DeviceCatalog")!.SelectedIndex=0;
            window.FindControl<TextBox>("StationName")!.Text="DriveX";window.FindControl<TextBox>("StationAlias")!.Text="1";window.AddConfiguredStation();
            var driveId=project.Hardware.Stations![0].Id;
            window.FindControl<TextBox>("StationName")!.Text="IoNode";window.FindControl<TextBox>("StationAlias")!.Text="2";window.AddConfiguredStation();
            var ioId=project.Hardware.Stations[1].Id;
            var networkStations=window.FindControl<ListBox>("NetworkStations")!;
            if(networkStations.ItemCount!=2)throw new Exception("FAIL: station views not synchronized");
            networkStations.SelectedIndex=1;window.MoveConfiguredStation(-1);
            if(project.Hardware.Stations[0].Id!=ioId||window.FindControl<ListBox>("ConfiguredStations")!.SelectedIndex!=0)throw new Exception("FAIL: network reorder does not update hardware view");
            window.FindControl<TextBox>("StationName")!.Text="IoRenamed";window.ApplyStationProperties();
            if(!networkStations.ItemsSource!.Cast<string>().First().Contains("IoRenamed"))throw new Exception("FAIL: hardware rename not reflected in network");
            var stationCombo=window.FindControl<ComboBox>("IoStation")!;
            stationCombo.SelectedItem=stationCombo.ItemsSource!.Cast<object>().Single(c=>Equals(c.GetType().GetProperty("Id")!.GetValue(c),driveId));
            window.FindControl<ListBox>("IoMappings")!.SelectedIndex=0;
            window.FindControl<TextBox>("IoName")!.Text="Ready";window.FindControl<TextBox>("IoAddress")!.Text="%IX0.0";window.FindControl<ComboBox>("IoPdo")!.SelectedIndex=0;window.SaveIoProperties(true);
            if(project.Hardware.Io[0].StationId!=driveId)throw new Exception("FAIL: IO does not retain station PDO binding");
            networkStations.SelectedIndex=1;
            var check=window.FindControl<StackPanel>("StationPdos")!.Children.OfType<CheckBox>().Single(c=>Equals(c.Tag,input));check.IsChecked=false;
            Reject(window.ApplyStationProperties,"bound PDO removal");
            if(!project.Hardware.Stations.Single(s=>s.Id==driveId).EnabledEntries.Contains(input))throw new Exception("FAIL: invalid PDO change committed");
            Reject(window.RemoveConfiguredStation,"bound station deletion");check.IsChecked=true;
            window.FindControl<ListBox>("IoMappings")!.SelectedIndex=0;window.FindControl<TextBox>("IoAddress")!.Text="%QX0.0";
            Reject(()=>window.SaveIoProperties(true),"IO direction mismatch");
            if(project.Hardware.Io[0].Address!="%IX0.0")throw new Exception("FAIL: invalid IO edit not rolled back");
            var axisCombo=window.FindControl<ComboBox>("AxisStation")!;
            axisCombo.SelectedItem=axisCombo.ItemsSource!.Cast<object>().Single(c=>Equals(c.GetType().GetProperty("Id")!.GetValue(c),driveId));
            window.FindControl<TextBox>("AxisName")!.Text="X";window.FindControl<TextBox>("ControllerAxis")!.Text="Axis1";window.SaveAxisProperties(false);
            if(project.Hardware.Axes[0].StationId!=driveId)throw new Exception("FAIL: axis station binding missing");
            window.FindControl<TextBox>("NetworkManagementHost")!.Text="bad;command";
            Reject(window.ApplyNetworkConfiguration,"invalid management host");
            if(project.Network is not null)throw new Exception("FAIL: invalid network configuration committed");
            window.FindControl<TextBox>("NetworkManagementHost")!.Text="192.168.1.42";
            window.FindControl<TextBox>("NetworkSshUser")!.Text="a";
            window.FindControl<TextBox>("NetworkRuntimeEndpoint")!.Text="https://192.168.1.42:8443";
            window.FindControl<TextBox>("EtherCatInterface")!.Text="enp2s0";
            Field("debugSessionToken").SetValue(window,"old-session");((TextBox)Field("password").GetValue(window)!).Text="old-password";
            window.ApplyNetworkConfiguration();
            if(((TextBox)Field("sshHost").GetValue(window)!).Text!="192.168.1.42"||Field("debugSessionToken").GetValue(window) is not null||((TextBox)Field("password").GetValue(window)!).Text!="")throw new Exception("FAIL: network apply does not invalidate old authentication");
            if(!window.FindControl<TextBlock>("NetworkConfigurationStatus")!.Text!.Contains("尚未应用"))throw new Exception("FAIL: offline network change claims device application");
            Field("debugSessionToken").SetValue(window,"old-endpoint-session");
            window.FindControl<TextBox>("NetworkRuntimeEndpoint")!.Text="";window.ApplyNetworkConfiguration();
            if(((TextBox)Field("endpoint").GetValue(window)!).Text!=""||Field("debugSessionToken").GetValue(window) is not null)throw new Exception("FAIL: clearing direct endpoint retains old connection identity");
            var authenticated=new OpenPlcClient(new Uri("https://192.168.1.42:8443"));authenticated.UseSessionToken("test-session");Field("runtime").SetValue(window,authenticated);
            Method("RefreshNetworkTopology").Invoke(window,null);
            ((TextBox)Field("sshHost").GetValue(window)!).Text="192.168.1.56";
            var actual=window.FindControl<TextBlock>("NetworkActualConnection")!.Text!;
            if(!actual.Contains("192.168.1.56")||actual.Contains("Runtime 已认证"))throw new Exception("FAIL: network connection status stale after tools target change");
            window.FindControl<TextBox>("NetworkManagementHost")!.Text="192.168.1.57";
            Reject(()=>Pump(window.ConnectConfiguredRuntimeAsync()),"unapplied network draft connection");
            window.FindControl<TextBox>("NetworkManagementHost")!.Text="192.168.1.42";window.ApplyNetworkConfiguration();
            var directory=Path.GetFullPath("artifacts/configuration-ui-project");Directory.CreateDirectory(directory);var file=Path.Combine(directory,"Configuration.stproj");
            Pump(window.SaveAtAsync(file));
            Pump(window.LoadAtAsync(file));
            project=(StProject)Field("project").GetValue(window)!;
            if(project.Network?.EtherCatInterface!="enp2s0"||project.Hardware.Stations![0].Name!="IoRenamed"||project.Hardware.Axes[0].StationId!=driveId)throw new Exception("FAIL: configuration did not survive save/reopen");
            tabs.SelectedIndex=2;Dispatcher.UIThread.RunJobs();window.FindControl<ScrollViewer>("HardwareConfigurationScroll")!.Offset=default;Dispatcher.UIThread.RunJobs();using(var frame=window.CaptureRenderedFrame())frame?.Save("artifacts/st-workbench-hardware.png",Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            tabs.SelectedIndex=6;Dispatcher.UIThread.RunJobs();window.FindControl<ScrollViewer>("NetworkConfigurationScroll")!.Offset=default;Dispatcher.UIThread.RunJobs();using(var frame=window.CaptureRenderedFrame())frame?.Save("artifacts/st-workbench-network.png",Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Console.WriteLine("PASS hardware/network controls, ordering, cross-page edits, PDO/axis binding, invalid change rollback, connection identity and persistence");
        }
        finally { window.Close(); }
    }
    private static FieldInfo Field(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name)=>typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!;
    private static void Reject(Action action,string name){try{action();}catch(InvalidOperationException){return;}catch(InvalidDataException){return;}throw new Exception("FAIL: did not reject "+name);}
    private static void Pump(Task task){var end=DateTime.UtcNow.AddSeconds(30);while(!task.IsCompleted&&DateTime.UtcNow<end){Dispatcher.UIThread.RunJobs();Thread.Sleep(1);}if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();}
}

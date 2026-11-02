using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class HardwareUploadUiTests
{
    private static FieldInfo Field(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    private static void Until(Func<bool> done){var end=DateTime.UtcNow.AddSeconds(8);while(!done()&&DateTime.UtcNow<end){Dispatcher.UIThread.RunJobs();Thread.Sleep(1);}if(!done())throw new TimeoutException("Hardware upload did not finish without a file picker");}
    public static void Run(){foreach(var scenario in new[]{"apply","cancel","edit","target","invalid"})Run(scenario);Console.WriteLine("PASS hardware upload directly updates current project, preserves sources, and guards cancellation/edit/target/invalid configuration");}
    private static void Run(string scenario)
    {
        var calls=new List<string>();
        var hardware=new HardwareConfiguration{Axes=[new("UploadedAxis","Axis1","mm",scenario=="invalid"?20:-20,10) { MaximumJerk = 12, Runtime = new() { DriveDemandProtection = true, DriveErrorOnly = true, DemandDeliveryEnforced = false, FollowingErrorRawPerUnit = 100, PositionDemandRawPerUnit = 200, VelocityFeedbackRawPerUnitPerSecond = 123, VelocityOffsetRawPerUnitPerSecond = 456, VelocityFeedforwardGain = .75, DriveFollowingErrorWindowRaw = 321 } }]};
        var document=new HardwareConfigurationDocument(hardware);
        var window=new MainWindow((target,request)=>{calls.Add(request.Action);return Task.FromResult(new DeploymentResponse(true,"hardware-exported",request.RequestId,"",null,null,Transfer:JsonSerializer.SerializeToElement(new{configuration=document})));});window.Show();
        try{
            var project=(StProject)Field("project").GetValue(window)!;var oldHardware=project.Hardware;
            var source=window.FindControl<TextEditor>("SourceEditor")!.Text;
            Field("hardwareTransferScope").SetValue(window,true);
            var operation=(Task)typeof(MainWindow).GetMethod("UploadSelectedAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
            Until(()=>operation.IsCompleted||window.OwnedWindows.Any());
            var prompt=window.OwnedWindows.Single();
            var message=string.Join("\n",prompt.GetVisualDescendants().OfType<TextBlock>().Select(t=>t.Text));
            if(message.Contains("独立文件")||message.Contains("JSON"))throw new Exception("FAIL: hardware upload still requires an external file");
            if(scenario=="edit")window.FindControl<TextEditor>("SourceEditor")!.Text+="\n(* local edit during upload *)";
            if(scenario=="target")((TextBox)Field("sshHost").GetValue(window)!).Text="192.168.1.99";
            var buttons=prompt.GetVisualDescendants().OfType<Button>();
            buttons.Single(b=>scenario=="cancel"?Equals(b.Content,"取消"):!Equals(b.Content,"取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(()=>operation.IsCompleted||window.OwnedWindows.Any());
            if(window.OwnedWindows.Any())throw new Exception("FAIL: hardware upload requested another dialog");
            if(scenario is "edit" or "target" or "invalid"){
                try{operation.GetAwaiter().GetResult();throw new Exception("FAIL: changed or invalid hardware upload accepted");}
                catch(Exception e)when(e is InvalidOperationException or InvalidDataException){}
            }else operation.GetAwaiter().GetResult();
            if(scenario=="apply"){
                if(project.Hardware.Axes.Single().Name!="UploadedAxis"||!(bool)Field("dirty").GetValue(window)!)throw new Exception("FAIL: uploaded hardware not applied to current unsaved project");
                if(project.Hardware.Axes[0] is not { MaximumJerk: 12, Runtime: { VelocityFeedforwardGain: .75, DriveFollowingErrorWindowRaw: 321, DemandDeliveryEnforced: false } }) throw new Exception("FAIL: uploaded migration fields lost");
                if(window.FindControl<TextEditor>("SourceEditor")!.Text!=source)throw new Exception("FAIL: hardware upload modified ST");
            }else if(!ReferenceEquals(project.Hardware,oldHardware))throw new Exception("FAIL: refused upload changed local hardware");
            if(!calls.SequenceEqual(new[]{"hardware-export"}))throw new Exception("FAIL: upload mutated PLC");
        }finally{Field("allowClose").SetValue(window,true);foreach(var prompt in window.OwnedWindows.ToArray())prompt.Close();window.Close();}
    }
}

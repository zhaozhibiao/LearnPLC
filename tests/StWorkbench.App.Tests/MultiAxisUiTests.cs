using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using StWorkbench.App;
using StWorkbench.Core;

public static class MultiAxisUiTests
{
    public static void Run()
    {
        var window=new MainWindow { Height=720 };window.Show();
        try
        {
            var library=window.FindControl<TreeView>("InstructionLibrary")!;
            foreach(var name in new[]{"X","Y","Z"})
            {
                window.FindControl<TextBox>("AxisName")!.Text=name;
                window.FindControl<TextBox>("ControllerAxis")!.Text="Axis"+name;
                window.SaveAxisProperties(false);Dispatcher.UIThread.RunJobs();
            }
            var group=((IEnumerable<TreeViewItem>)library.ItemsSource!).Single(g=>Equals(g.Header,"工程轴"));
            if(((IEnumerable<TreeViewItem>)group.ItemsSource!).Count()!=27)throw new Exception("FAIL: multi-axis instruction generation missing");
            var node=((IEnumerable<TreeViewItem>)group.ItemsSource!).Single(n=>n.Tag is StInstruction{Id:"Axis_Y_MC_MoveAbsolute"});
            library.SelectedItem=node;Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"MULTI_AXIS tree bounds {library.Bounds.Width} x {library.Bounds.Height}");
            using(var frame=window.CaptureRenderedFrame())frame?.Save("artifacts/st-workbench-multi-axis-before.png",Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            if(library.Bounds.Height<80)throw new Exception("FAIL: multi-axis instruction list is squeezed out by preview");
            var editor=window.FindControl<TextEditor>("SourceEditor")!;
            window.InsertInstruction("Axis_Y_MC_MoveAbsolute",editor.Text.IndexOf("END_PROGRAM",StringComparison.Ordinal));
            if(!editor.Text.Contains("Axis := AXIS_Y"))throw new Exception("FAIL: multi-axis selection loses binding");
            Dispatcher.UIThread.RunJobs();
            if(library.Bounds.Height<80)throw new Exception("FAIL: insertion result hides instruction list");
            window.Width=1080;Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"MULTI_AXIS minimum window tree height {library.Bounds.Height}");
            if(library.Bounds.Height<80)throw new Exception("FAIL: minimum-size window hides multi-axis instructions");
            window.Width=1440;Dispatcher.UIThread.RunJobs();
            var preview=window.FindControl<Button>("InstructionPreviewButton")!;var height=library.Bounds.Height;
            preview.Flyout!.ShowAt(preview);Dispatcher.UIThread.RunJobs();
            if(!preview.Flyout.IsOpen||library.Bounds.Height!=height)throw new Exception("FAIL: preview must not consume instruction viewport");
            preview.Flyout.Hide();Dispatcher.UIThread.RunJobs();
            window.FindControl<TextBox>("AxisName")!.Text="W";window.FindControl<TextBox>("ControllerAxis")!.Text="AxisX";
            try { window.SaveAxisProperties(false);throw new Exception("FAIL: duplicate controller mapping allowed"); }catch(InvalidDataException){}
            if(!window.FindControl<TextBlock>("HardwareConfigurationStatus")!.Text!.Contains("未应用"))throw new Exception("FAIL: rejected axis addition needs visible explanation");
            if(!window.FindControl<TextBlock>("AxisInstructionHint")!.Text!.Contains("3 个"))throw new Exception("FAIL: failed addition changed axis count");
            using(var frame=window.CaptureRenderedFrame())frame?.Save("artifacts/st-workbench-multi-axis.png",Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Console.WriteLine("PASS multi-axis instruction count, visible viewport and exact axis binding");
        }
        finally{window.Close();}
    }
}

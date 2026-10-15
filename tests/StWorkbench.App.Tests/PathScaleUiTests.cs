using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using StWorkbench.App;
using StWorkbench.Core;

public static class PathScaleUiTests
{
    public static void Run()
    {
        var window=new MainWindow();window.Show();
        try
        {
            var project=(StProject)typeof(MainWindow).GetField("project",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            project.Hardware.Axes.AddRange([new("X","Axis1","mm",-100,100),new("Y","Axis2","mm",-100,100)]);
            window.ImportPathText("scale.dxf","0\nSECTION\n2\nENTITIES\n0\nLINE\n8\ncut\n10\n0\n20\n0\n11\n10\n21\n0\n0\nENDSEC\n0\nEOF\n");
            var scale=window.FindControl<TextBox>("PathScale")!;
            var status=window.FindControl<TextBlock>("PathStatus")!;
            scale.Text="1:1";Preview(window);var first=Seconds(status.Text!);
            scale.Text="2";
            if(!window.FindControl<TextBlock>("PathPlanningTime")!.Text!.Contains("待重新计算")||Regex.IsMatch(status.Text!,@"\d+\.\d+s"))throw new Exception("FAIL: changed scale retains stale planning time");
            Preview(window);var second=Seconds(status.Text!);
            Console.WriteLine($"PATH_SCALE 1 -> 2: {first}s -> {second}s");
            if(second<=first)throw new Exception("FAIL: doubled DXF geometry must change planned duration");
            scale.Text="10:1";Preview(window);var tenfold=Seconds(status.Text!);
            Console.WriteLine($"PATH_SCALE ratio 1:1 -> 10:1: {first}s -> {tenfold}s");
            if(tenfold<=second)throw new Exception("FAIL: ratio notation must scale geometry");
            scale.Text="10：1";Preview(window);if(Seconds(status.Text!)!=tenfold)throw new Exception("FAIL: full-width ratio separator");
            scale.Text="1:10";Preview(window);if(Seconds(status.Text!)>=first)throw new Exception("FAIL: ratio reduction must shorten trajectory");
            foreach(var invalid in new[]{"0","-1","10:0","1:2:3","NaN"})
            {
                scale.Text=invalid;try{Preview(window);throw new Exception("FAIL: invalid ratio accepted "+invalid);}catch(InvalidDataException){}
                if(!window.FindControl<TextBlock>("PathPlanningTime")!.Text!.Contains("无法计算"))throw new Exception("FAIL: invalid ratio kept valid time");
            }
            scale.Text="2";Preview(window);
            window.PrestorePathDraft();var stored=project.Paths.Single();
            scale.Text="4";Preview(window);var third=Seconds(status.Text!);
            Console.WriteLine($"PATH_SCALE normalized 2 -> 4: {second}s -> {third}s");
            if(third<=second||stored.Segments[0].End.X!=20)throw new Exception("FAIL: stored geometry scaling or immutable asset changed");
            scale.Text="100";
            try{Preview(window);throw new Exception("FAIL: exceeded axis bounds accepted");}catch(InvalidDataException){}
            Console.WriteLine("PATH_SCALE failed replan status: "+status.Text);
            if(!status.Text!.Contains("失败")||Regex.IsMatch(status.Text,@"\d+\.\d+s"))throw new Exception("FAIL: failed replan still displays previous duration");
            if(!status.Text.Contains("轴限位"))throw new Exception("FAIL: geometry bounds error needs actionable explanation");
            scale.Text="2";
            var profile=(TextBox[])typeof(MainWindow).GetField("pathProfile",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;profile[0].Text="0.001";
            try{Preview(window);throw new Exception("FAIL: oversized preparation budget accepted");}catch(InvalidDataException){}
            if(!status.Text!.Contains("采样上限")||Regex.IsMatch(status.Text,@"\d+\.\d+s"))throw new Exception("FAIL: oversized plan needs explicit unavailable time");
            Console.WriteLine("PASS DXF scale/time, normalized geometry and failed replan invalidation");
        }
        finally{window.Close();}
    }
    private static double Seconds(string text)=>double.Parse(Regex.Match(text,@"(\d+\.\d+)s").Groups[1].Value,CultureInfo.InvariantCulture);
    private static void Preview(MainWindow window)
    {
        try{typeof(MainWindow).GetMethod("PreviewPathDraft",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);Dispatcher.UIThread.RunJobs();}
        catch(TargetInvocationException error)when(error.InnerException is InvalidDataException){throw (InvalidDataException)error.InnerException;}
    }
}

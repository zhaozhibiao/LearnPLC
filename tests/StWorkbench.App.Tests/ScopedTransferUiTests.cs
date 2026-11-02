using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StWorkbench.App;
using StWorkbench.Toolchain;

internal static class ScopedTransferUiTests
{
    public static void Run()
    {
        var window=new MainWindow();window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var download=window.FindControl<Button>("DownloadButton")!;
            var upload=window.FindControl<Button>("UploadButton") ?? throw new Exception("FAIL: program upload action missing");
            if(!Equals(download.Content,"下载程序")||!Equals(upload.Content,"上传程序")) throw new Exception("FAIL: default transfer scope is unclear");
            var buttons=window.GetVisualDescendants().OfType<Button>().ToArray();
            if(buttons.Any(b=>Equals(b.Content,"生成候选包"))||!buttons.Any(b=>Equals(b.Content,"编译"))) throw new Exception("FAIL: compile and package remain separate user actions");
            var tree=(TreeView)typeof(MainWindow).GetField("tree",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            var roots=((IEnumerable<TreeViewItem>)tree.ItemsSource!).ToArray();
            tree.SelectedItem=roots.Single(n=>Equals(n.Header,"硬件"));Dispatcher.UIThread.RunJobs();
            if(!Equals(download.Content,"下载硬件")||!Equals(upload.Content,"上传硬件")) throw new Exception("FAIL: hardware selection did not scope transfer");
            tree.SelectedItem=roots[0];Dispatcher.UIThread.RunJobs();
            if(!Equals(download.Content,"下载程序")||!Equals(upload.Content,"上传程序")) throw new Exception("FAIL: program selection retained hardware scope");
            var compile=(Task)typeof(MainWindow).GetMethod("CompileAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
            var deadline=DateTime.UtcNow.AddSeconds(90);
            while(!compile.IsCompleted&&DateTime.UtcNow<deadline){Dispatcher.UIThread.RunJobs();Thread.Sleep(2);}
            if(!compile.IsCompleted)throw new TimeoutException("Compile/package UI verification timed out");compile.GetAwaiter().GetResult();
            var package=(PackageResult?)typeof(MainWindow).GetField("candidatePackage",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
            if(package is not {Success:true}||!File.Exists(package.PackagePath))throw new Exception("FAIL: one compile action did not create a current candidate package");
            using(var zip=System.IO.Compression.ZipFile.OpenRead(package.PackagePath))if(zip.GetEntry("engineering-archive.json") is null)throw new Exception("FAIL: compiled candidate has no uploadable ST archive");
            Console.WriteLine("PASS contextual program/hardware transfer labels and single compile/package action");
        }
        finally { typeof(MainWindow).GetField("allowClose",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);window.Close(); }
    }
}

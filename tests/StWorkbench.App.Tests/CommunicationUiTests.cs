using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.Toolchain;
using AvaloniaEdit;

internal static class CommunicationUiTests
{
    public static void Run()
    {
        var window = new MainWindow(); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var project = (StProject)typeof(MainWindow).GetField("project", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            void Invoke(string method, params object[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
            window.FindControl<TextBox>("CommunicationName")!.Text = "Bus";
            window.FindControl<TextBox>("CommunicationHost")!.Text = "127.0.0.1";
            Invoke("ApplyCommunicationChannel", false);
            if (project.Communication?.Channels.Single().Name != "Bus") throw new Exception("Communication UI add failed");
            var list = window.FindControl<ListBox>("CommunicationChannels")!; list.SelectedIndex = 0;
            window.FindControl<TextBox>("CommunicationPort")!.Text = "15020";
            Invoke("ApplyCommunicationChannel", true);
            if (project.Communication.Channels[0].Port != 15020) throw new Exception("Communication UI edit failed");
            list.SelectedIndex = 0; window.FindControl<TextBox>("CommunicationHost")!.Text = "invalid;host";
            try { Invoke("ApplyCommunicationChannel", true); throw new Exception("Invalid communication accepted"); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidDataException or InvalidOperationException) { }
            if (project.Communication.Channels[0].Host != "127.0.0.1") throw new Exception("Invalid edit not rolled back");
            var file = Path.GetFullPath("artifacts/communication-ui/P.stproj");
            Pump(window.SaveAtAsync(file)); Pump(window.LoadAtAsync(file));
            project = (StProject)typeof(MainWindow).GetField("project", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            if (project.Communication?.Channels[0].Port != 15020) throw new Exception("Communication UI save/reopen failed");
            var templates = BuiltinLibraryCatalog.CommunicationInstructions(project.Communication).ToArray();
            if (!templates.Any(t => t.Id == "Comm_Bus_WB_MB_REQUEST") || !templates.Any(t => t.Id == "Comm_Bus_WB_COMM_CLOSE")) throw new Exception("Configured channel resource missing");
            var editor = window.FindControl<TextEditor>("SourceEditor")!;
            window.InsertInstruction("Comm_Bus_WB_MB_REQUEST",editor.Text.IndexOf("END_PROGRAM",StringComparison.Ordinal));
            var snapshot = ProjectValidator.Capture(project, ((Dictionary<Guid,SourceDocument>)Field("sources").GetValue(window)!).Values.ToArray());
            Field("candidatePackage").SetValue(window,new PackageResult(new(true,[],"","","",""),"fixture",new string('A',64),""));
            Field("candidateSourceHash").SetValue(window,snapshot.SourceHash);Field("candidateHardwareHash").SetValue(window,snapshot.HardwareHash);
            foreach (bool? ready in new bool?[] { null,false,true })
            {
                Invoke("ApplyCompiledCandidate",new DeploymentResponse(true,"compiled","fixture","",null,null,CommunicationReady:ready,CommunicationMessage:"fixture unavailable"));
                if (((Button)Field("activateButton").GetValue(window)!).IsEnabled != (ready==true)) throw new Exception("Communication readiness must be explicit before activation");
                if (ready!=true && !window.FindControl<TextBlock>("BuildStatus")!.Text!.Contains("通信工作进程未就绪")) throw new Exception("Communication unavailable status missing");
            }
            editor.Document.UndoStack.Undo();
            window.FindControl<ListBox>("CommunicationChannels")!.SelectedIndex = 0; Invoke("DeleteCommunicationChannel");
            if (project.Communication is not null) throw new Exception("Communication UI delete failed");
            Console.WriteLine("PASS communication UI add/edit, invalid rollback, save/reopen, channel resources and delete");
        }
        finally { window.Close(); }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    private static void Pump(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult();
    }
}

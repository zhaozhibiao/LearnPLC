using System.Reflection;
using System.Net.WebSockets;
using Avalonia.Controls;
using Avalonia.Threading;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.OpenPlc;
using StWorkbench.Toolchain;

internal static class OnlineWriteUiTests
{
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Invoke(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    public static void Run()
    {
        var window = new MainWindow(); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var button = window.FindControl<Button>("OnlineWriteOnce");
            if (button is null) throw new Exception("Online BOOL one-shot write section is missing");
            var choices = window.FindControl<ComboBox>("OnlineWriteSymbol")!;
            if (button.IsEnabled || choices.ItemCount != 0) throw new Exception("Unbound monitor exposed writes");
            var calls = 0;
            Field("onlineBooleanWriter").SetValue(window, (Func<DebugSocketClient, string, DebugVariableAddress, bool, Task>)((_, md5, address, value) =>
            { if (md5 != new string('a',32) || address.ElementIndex != 1 || !value) throw new Exception("Wrong exact write target"); calls++; return Task.CompletedTask; }));
            Pump((Task)Invoke(window,"WriteOnlineBooleanAsync")!);
            if(calls != 0) throw new Exception("Unbound write sent a request");

            var project = (StProject)Field("project").GetValue(window)!;
            var item = project.Items.First();
            var documents = new Dictionary<Guid, SourceDocument> { [item.Id] = new(item.Id,item.SourcePath,
                "PROGRAM Main VAR ParametersConfirmed : BOOL; RunOnce : BOOL; AB_HIDDEN : BOOL; BadSize : BOOL; Number : INT; END_VAR END_PROGRAM FUNCTION_BLOCK Child VAR ParametersConfirmed : BOOL; END_VAR END_FUNCTION_BLOCK") };
            Field("sources").SetValue(window,documents);
            var snapshot = ProjectValidator.Capture(project,documents.Values.ToArray());
            Field("candidatePackage").SetValue(window,new PackageResult(new(true,[],"","","",""),"fixture","build","symbols"));
            Field("candidateSourceHash").SetValue(window,snapshot.SourceHash); Field("candidateHardwareHash").SetValue(window,snapshot.HardwareHash);
            Field("activeBuildHash").SetValue(window,"build"); Field("activeProgramMd5").SetValue(window,new string('a',32)); Field("debugSessionToken").SetValue(window,"fixture");
            var client = new DebugSocketClient(new Uri("https://localhost"),transport:new OpenSocket());
            typeof(DebugSocketClient).GetField("authenticated",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,true);
            Field("debugClient").SetValue(window,client);
            var identity = new MonitorIdentity(snapshot.SourceHash,"symbols","session");
            var session = new ReadOnlyMonitorSession(identity,identity,TimeSpan.FromSeconds(2));
            Field("monitorSession").SetValue(window,session);
            DebugSymbol[] symbols = [new("INSTANCE0.PARAMETERSCONFIRMED","BOOL",new(0,1,1)), new("INSTANCE0.RUNONCE","BOOL",new(0,2,1)),new("AB_GLOBAL","BOOL",new(0,3,1)),new("INSTANCE0.AB_HIDDEN","BOOL",new(0,4,1)),new("INSTANCE0.BADSIZE","BOOL",new(0,5,2)),new("INSTANCE0.NUMBER","INT",new(0,6,1)),new("INSTANCE0.CHILD.PARAMETERSCONFIRMED","BOOL",new(0,7,1))];
            Field("debugSymbols").SetValue(window,symbols);
            void Sample(long sequence, string value = "FALSE") => session.Observe(identity,new(symbols[0].Path,"BOOL",value,sequence,null,DateTimeOffset.UtcNow,SampleQuality.Good));
            Sample(1); Invoke(window,"RefreshOnlineWrite");
            if(choices.ItemCount != 2) throw new Exception("BOOL filter leaked internal, nested, non-BOOL or wrong-size symbols");
            choices.SelectedIndex=0; window.FindControl<ComboBox>("OnlineWriteValue")!.SelectedIndex=0;
            Invoke(window,"RefreshOnlineWrite");
            if(!button.IsEnabled || !window.FindControl<TextBlock>("OnlineWriteCurrent")!.Text!.Contains("FALSE")) throw new Exception("Fresh BOOL selection unavailable");
            Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_, message, _) => { if(!message.Contains("Main.ParametersConfirmed") || !message.Contains("TRUE") || !message.Contains(new string('a',32))) throw new Exception("Confirmation omits exact identity/value"); return Task.FromResult(false); }));
            Pump((Task)Invoke(window,"WriteOnlineBooleanAsync")!);
            if(calls!=0) throw new Exception("Cancel sent a write");
            var localCandidate=Field("candidatePackage").GetValue(window);
            using(var engineering=new EngineeringClient(new Uri("https://localhost:18445"),"paired-device-test-token",httpClient:new HttpClient()))
            {
                Field("engineeringClient").SetValue(window,engineering);Field("candidatePackage").SetValue(window,null);
                Field("onlineBoundBuildHash").SetValue(window,"build");Field("onlineBoundHardwareHash").SetValue(window,snapshot.HardwareHash);
                Invoke(window,"RefreshOnlineWrite");
                if(!button.IsEnabled)throw new Exception("Engineering online wrongly requires a local candidate");
                Pump((Task)Invoke(window,"WriteOnlineBooleanAsync")!);
                if(calls!=0)throw new Exception("Engineering confirmation cancel sent a write");
                Field("engineeringClient").SetValue(window,null);Field("candidatePackage").SetValue(window,localCandidate);
            }
            Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,_,_) => { Field("editGeneration").SetValue(window,(long)Field("editGeneration").GetValue(window)!+1); return Task.FromResult(true); }));
            Pump((Task)Invoke(window,"WriteOnlineBooleanAsync")!);
            if(calls!=0) throw new Exception("Confirmation accepted stale project");
            foreach(var guard in new[] { "connectionGeneration", "deploymentBusy", "activeProgramMd5", "candidatePackage", "monitorSession", "selection", "expired" })
            {
                var original = guard is "selection" or "expired" ? null : Field(guard).GetValue(window);
                Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,_,_) =>
                {
                    switch(guard)
                    {
                        case "connectionGeneration": Field(guard).SetValue(window,(long)original!+1); break;
                        case "deploymentBusy": Field(guard).SetValue(window,true); break;
                        case "activeProgramMd5": Field(guard).SetValue(window,new string('b',32)); break;
                        case "candidatePackage": Field(guard).SetValue(window,((PackageResult)original!) with { }); break;
                        case "monitorSession": Field(guard).SetValue(window,new ReadOnlyMonitorSession(identity,identity,TimeSpan.FromSeconds(2))); break;
                        case "selection": choices.SelectedIndex=1; break;
                        case "expired": session.Observe(identity,new(symbols[0].Path,"BOOL","FALSE",100,null,DateTimeOffset.UtcNow.AddSeconds(-5),SampleQuality.Good)); break;
                    }
                    return Task.FromResult(true);
                }));
                Pump((Task)Invoke(window,"WriteOnlineBooleanAsync")!);
                if(calls!=0) throw new Exception("Stale confirmation guard failed: " + guard);
                if(guard is not ("selection" or "expired")) Field(guard).SetValue(window,original);
                Sample(guard == "expired" ? 101 : 20 + Array.IndexOf(new[] { "connectionGeneration", "deploymentBusy", "activeProgramMd5", "candidatePackage", "monitorSession", "selection", "expired" },guard));
                Invoke(window,"RefreshOnlineWrite"); choices.SelectedIndex=0;
            }
            Field("onlineBooleanWriter").SetValue(window, (Func<DebugSocketClient,string,DebugVariableAddress,bool,Task>)((_, md5,address,value) =>
            {
                if(md5!=new string('a',32) || address.ElementIndex!=1 || !value) throw new Exception("Wrong write target");
                calls++; Sample(102,"TRUE"); return Task.CompletedTask;
            }));
            Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,_,_) => Task.FromResult(true)));
            Pump((Task)Invoke(window,"WriteOnlineBooleanAsync")!);
            if(calls!=1 || !window.FindControl<TextBlock>("OnlineWriteStatus")!.Text!.Contains("等待")) throw new Exception("Write must be sent once and await readback");
            Invoke(window,"RefreshOnlineWrite");
            if(choices.SelectedIndex!=0 || !window.FindControl<TextBlock>("OnlineWriteStatus")!.Text!.Contains("等待")) throw new Exception("Poll lost selection or claimed immediate application");
            Sample(103,"TRUE"); Invoke(window,"RefreshOnlineWrite");
            if(!window.FindControl<TextBlock>("OnlineWriteStatus")!.Text!.Contains("读回")) throw new Exception("Subsequent sample did not confirm readback");
            session.Observe(identity,new(symbols[1].Path,"BOOL","FALSE",104,null,DateTimeOffset.UtcNow,SampleQuality.Good)); choices.SelectedIndex=1;
            Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,message,_) => { if(!message.Contains("可能启用运动")) throw new Exception("RunOnce motion warning missing"); return Task.FromResult(false); }));
            Pump((Task)Invoke(window,"WriteOnlineBooleanAsync")!);
            if(calls!=1) throw new Exception("RunOnce cancel sent a write");
            choices.SelectedIndex=0; Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,_,_) => Task.FromResult(true)));
            Field("onlineBooleanWriter").SetValue(window,(Func<DebugSocketClient,string,DebugVariableAddress,bool,Task>)((_,_,_,_) => { calls++; throw new TimeoutException("fixture"); }));
            Pump((Task)Invoke(window,"WriteOnlineBooleanAsync")!);
            if(calls!=2 || button.IsEnabled || choices.ItemCount!=0 || !window.FindControl<TextBlock>("OnlineWriteStatus")!.Text!.Contains("结果未知")) throw new Exception("Timeout retried or retained live monitor");
            Console.WriteLine("PASS online BOOL filtering, readonly unbound guard, confirmation cancellation, stale confirmation guard, exact single write, readback and unknown timeout invalidation");
        }
        finally { window.Close(); }
    }
    private static void Pump(Task task) { while(!task.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); } task.GetAwaiter().GetResult(); }
    private sealed class OpenSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status,string? description,CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status,string? description,CancellationToken token) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken token) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType type,bool end,CancellationToken token) => throw new NotSupportedException();
    }
}

using System.Net.WebSockets;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.OpenPlc;
using StWorkbench.Toolchain;

internal static class EditorOnlineWriteUiTests
{
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    private static object? Invoke(MainWindow window,string name,params object[] args) => typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,args);
    public static void Run()
    {
        var window = new MainWindow(); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var editor = window.FindControl<TextEditor>("SourceEditor")!;
            var writeTrue = window.FindControl<MenuItem>("EditorWriteTrue");
            if(writeTrue is null) throw new Exception("Editor right-click one-shot BOOL write menu is missing");
            var writeFalse = window.FindControl<MenuItem>("EditorWriteFalse")!;
            var main = new ProjectItem { Name="Main", SourcePath="sources/Main.st" };
            const string text = "PROGRAM Main\nVAR\n ParametersConfirmed : BOOL;\n RunOnce : BOOL;\n Number : INT;\n Block : Child;\n Text : STRING;\nEND_VAR\nParametersConfirmed := FALSE;\nNumber := 5;\n(* ParametersConfirmed *)\nText := 'ParametersConfirmed';\nBlock.ParametersConfirmed := FALSE;\nUnknown := FALSE;\nEND_PROGRAM\nFUNCTION_BLOCK Child\nVAR ParametersConfirmed : BOOL; END_VAR\nParametersConfirmed := FALSE;\nEND_FUNCTION_BLOCK\n";
            var project = new StProject { Items=[main], Tasks=[new PlcTask { ProgramId=main.Id }] };
            SourceDocument[] docs = [new(main.Id,main.SourcePath,text)];
            var path=Path.GetFullPath("artifacts/editor-online-write-tests/Fixture.stproj");
            Pump(new ProjectStore().SaveAsync(path,project,docs)); Pump(window.LoadAtAsync(path));
            var snapshot=ProjectValidator.Capture(project,docs);
            var candidate=new PackageResult(new(true,[],"","","",""),"fixture","build","symbols");
            Field("candidatePackage").SetValue(window,candidate); Field("candidateSourceHash").SetValue(window,snapshot.SourceHash); Field("candidateHardwareHash").SetValue(window,snapshot.HardwareHash);
            Field("activeBuildHash").SetValue(window,"build"); Field("activeProgramMd5").SetValue(window,new string('a',32)); Field("debugSessionToken").SetValue(window,"fixture");
            var client=new DebugSocketClient(new Uri("https://localhost"),transport:new OpenSocket());
            typeof(DebugSocketClient).GetField("authenticated",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,true); Field("debugClient").SetValue(window,client);
            var identity=new MonitorIdentity(snapshot.SourceHash,"symbols","session");
            var session=new ReadOnlyMonitorSession(identity,identity,TimeSpan.FromSeconds(2)); Field("monitorSession").SetValue(window,session);
            DebugSymbol[] symbols=[new("INSTANCE0.PARAMETERSCONFIRMED","BOOL",new(0,1,1)),new("INSTANCE0.RUNONCE","BOOL",new(0,2,1)),new("INSTANCE0.NUMBER","INT",new(0,3,2)),new("INSTANCE0.BLOCK.PARAMETERSCONFIRMED","BOOL",new(0,4,1))];
            Field("debugSymbols").SetValue(window,symbols);
            session.Observe(identity,new(symbols[0].Path,"BOOL","FALSE",1,null,DateTimeOffset.UtcNow,SampleQuality.Good));
            Invoke(window,"RefreshInstanceTree");
            var contexts=window.FindControl<ComboBox>("MonitorInstance")!; contexts.SelectedItem=contexts.Items.OfType<StMonitorContext>().First(c=>c.Owner=="Main");
            var calls=0; var confirmations=0;
            Field("onlineBooleanWriter").SetValue(window,(Func<DebugSocketClient,string,DebugVariableAddress,bool,Task>)((_,_,_,_)=>{ calls++; return Task.CompletedTask; }));
            Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,message,_)=>{ confirmations++; if(!message.Contains("Main.ParametersConfirmed"))throw new Exception("Wrong confirmation symbol"); return Task.FromResult(false); }));
            var declaration=text.IndexOf("ParametersConfirmed",StringComparison.Ordinal)+4;
            var body=text.IndexOf("ParametersConfirmed :=",StringComparison.Ordinal)+4;
            RightClick(declaration,text.IndexOf("Number :",StringComparison.Ordinal)+2);
            if(!writeTrue.IsEnabled || !writeFalse.IsEnabled || !writeTrue.Header!.ToString()!.Contains("FALSE")) throw new Exception("Right click followed old caret or omitted current value");
            writeTrue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
            if(confirmations!=1 || calls!=0 || editor.Text!=text) throw new Exception("Editor confirmation cancel mutated source or sent transport request");
            editor.ContextMenu!.Close();
            RightClick(body,declaration); if(!writeTrue.IsEnabled)throw new Exception("Body BOOL reference not writable"); editor.ContextMenu.Close();
            foreach(var offset in new[] { text.IndexOf("Number :=",StringComparison.Ordinal)+2, text.IndexOf("(* ParametersConfirmed",StringComparison.Ordinal)+5, text.IndexOf("'ParametersConfirmed'",StringComparison.Ordinal)+5,
                text.LastIndexOf("ParametersConfirmed :=",StringComparison.Ordinal)+4, text.IndexOf("Block.ParametersConfirmed",StringComparison.Ordinal)+8,text.IndexOf("Unknown",StringComparison.Ordinal)+2,
                text.IndexOf("ParametersConfirmed :",StringComparison.Ordinal)+"ParametersConfirmed".Length })
            { RightClick(offset,declaration); if(writeTrue.IsEnabled || writeFalse.IsEnabled)throw new Exception("Unsafe editor target enabled at offset "+offset); editor.ContextMenu.Close(); }
            editor.CaretOffset=body; editor.RaiseEvent(new ContextRequestedEventArgs()); Dispatcher.UIThread.RunJobs();
            if(!writeTrue.IsEnabled)throw new Exception("Keyboard context menu did not resolve caret BOOL"); editor.ContextMenu.Close();
            Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,_,_)=>{ confirmations++; contexts.SelectedItem=contexts.Items.OfType<StMonitorContext>().First(c=>c.Owner=="Child"); return Task.FromResult(true); }));
            RightClick(body,declaration); writeTrue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
            if(confirmations!=2 || calls!=0)throw new Exception("Editor context switched during confirmation but write still sent"); editor.ContextMenu.Close();
            contexts.SelectedItem=contexts.Items.OfType<StMonitorContext>().First(c=>c.Owner=="Main");
            Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,_,_)=>{ confirmations++; return Task.FromResult(false); }));
            Field("onlineWriteConfirmation").SetValue(window,null);
            RightClick(body,declaration); writeTrue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var confirmation=window.OwnedWindows.Single();
            if(!confirmation.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("Main.ParametersConfirmed")==true && t.Text.Contains(new string('a',32))))throw new Exception("Actual editor confirmation omitted exact symbol/program identity");
            var cancel=confirmation.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"取消"));
            var cancelPoint=cancel.TranslatePoint(new Point(cancel.Bounds.Width/2,cancel.Bounds.Height/2),confirmation)!.Value;
            confirmation.MouseDown(cancelPoint,MouseButton.Left); confirmation.MouseUp(cancelPoint,MouseButton.Left); Dispatcher.UIThread.RunJobs();
            if(window.OwnedWindows.Count!=0 || calls!=0 || editor.Text!=text)throw new Exception("Real editor confirmation cancellation did not preserve source and transport");
            Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,_,_)=>{ confirmations++; return Task.FromResult(false); }));
            Field("deploymentBusy").SetValue(window,true); RightClick(body,declaration); if(writeTrue.IsEnabled)throw new Exception("Deployment busy editor menu enabled"); editor.ContextMenu.Close(); Field("deploymentBusy").SetValue(window,false);
            session.Observe(identity,new(symbols[0].Path,"BOOL","FALSE",2,null,DateTimeOffset.UtcNow.AddSeconds(-5),SampleQuality.Good));
            RightClick(body,declaration); if(writeTrue.IsEnabled)throw new Exception("Expired sample editor menu enabled"); editor.ContextMenu.Close();
            session.Observe(identity,new(symbols[0].Path,"BOOL","FALSE",3,null,DateTimeOffset.UtcNow,SampleQuality.Good));
            RightClick(body,declaration); Field("editGeneration").SetValue(window,(long)Field("editGeneration").GetValue(window)!+1);
            writeTrue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
            if(confirmations!=2 || calls!=0)throw new Exception("Menu opened before edit reused stale target"); editor.ContextMenu.Close();
            Field("onlineWriteConfirmation").SetValue(window,(Func<string,string,string,Task<bool>>)((_,message,_)=>Task.FromResult(message.Contains("FALSE"))));
            Field("onlineBooleanWriter").SetValue(window,(Func<DebugSocketClient,string,DebugVariableAddress,bool,Task>)((_,md5,address,value)=>
            { if(md5!=new string('a',32) || address.ElementIndex!=1 || value)throw new Exception("Editor FALSE route sent wrong exact target/value"); calls++; return Task.CompletedTask; }));
            RightClick(body,declaration); writeFalse.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
            if(calls!=1 || editor.Text!=text)throw new Exception("Editor FALSE action did not reuse single-write route"); editor.ContextMenu.Close();
            Field("debugClient").SetValue(window,null); RightClick(body,declaration); if(writeTrue.IsEnabled)throw new Exception("Offline menu enabled"); editor.ContextMenu.Close();
            var headers=editor.ContextMenu.Items.OfType<MenuItem>().Select(m=>m.Header?.ToString()).ToArray();
            foreach(var expected in new[]{"剪切","复制","粘贴","撤销","重做","全选"})if(!headers.Contains(expected))throw new Exception("Editor command lost: "+expected);
            if(editor.Text!=text)throw new Exception("Right-click commands changed ST source");
            Pump(client.DisposeAsync().AsTask());
            Console.WriteLine("PASS editor actual right-click target, declaration/body BOOL, keyboard target, comments/strings/scope/nested/numeric/offline/stale/deployment guards, existing editing commands and confirmation cancel without source or device writes");

            void RightClick(int offset,int oldCaret)
            {
                editor.CaretOffset=offset; editor.TextArea.TextView.EnsureVisualLines();
                var position=editor.TextArea.TextView.GetVisualPosition(editor.TextArea.Caret.Position,VisualYPosition.LineMiddle)-editor.TextArea.TextView.ScrollOffset;
                var point=editor.TextArea.TextView.TranslatePoint(position+new Point(1,0),window)!.Value;
                editor.CaretOffset=oldCaret;
                window.MouseDown(point,MouseButton.Right); window.MouseUp(point,MouseButton.Right); Dispatcher.UIThread.RunJobs();
            }
        }
        finally{ window.Close(); }
    }
    private static void Pump(Task task){var until=DateTime.UtcNow.AddSeconds(10);while(!task.IsCompleted && DateTime.UtcNow<until){Dispatcher.UIThread.RunJobs();Thread.Sleep(1);}task.GetAwaiter().GetResult();}
    private sealed class OpenSocket:WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus=>null; public override string? CloseStatusDescription=>null; public override WebSocketState State=>WebSocketState.Open; public override string? SubProtocol=>null;
        public override void Abort(){} public override void Dispose(){}
        public override Task CloseAsync(WebSocketCloseStatus status,string? description,CancellationToken token)=>Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status,string? description,CancellationToken token)=>Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken token)=>throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType type,bool end,CancellationToken token)=>throw new NotSupportedException();
    }
}

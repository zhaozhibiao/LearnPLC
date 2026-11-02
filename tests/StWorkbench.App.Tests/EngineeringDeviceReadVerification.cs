using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.OpenPlc;
using StWorkbench.Toolchain;

internal static class EngineeringDeviceReadVerification
{
    private static FieldInfo Field(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    private static object? Invoke(MainWindow window,string name)=>typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(window,null);
    private static void Pump(Task task)
    {
        var until=DateTime.UtcNow.AddSeconds(60);
        while(!task.IsCompleted&&DateTime.UtcNow<until){Dispatcher.UIThread.RunJobs();Thread.Sleep(2);}
        if(!task.IsCompleted)throw new TimeoutException("Read-only engineering verification timed out.");task.GetAwaiter().GetResult();
    }
    public static void Run()
    {
        var window=new MainWindow();window.Show();
        try
        {
            ((TextBox)Field("sshHost").GetValue(window)!).Text="192.168.1.31";((TextBox)Field("sshUser").GetValue(window)!).Text="a";
            if(Environment.GetEnvironmentVariable("ST_ENGINEERING_PAIR_ONCE")=="1")Pump((Task)Invoke(window,"PairEngineeringAsync")!);
            else Pump(window.ConnectEngineeringAsync());
            var service=(EngineeringClient)Field("engineeringClient").GetValue(window)!;
            var before=service.ExecuteAsync(new("status",Guid.NewGuid().ToString("N")));Pump(before);
            if(!before.Result.Success)throw new Exception("Real HTTPS status was not confirmed.");
            var debug=service.ExecuteAsync(new("program-debug-map",Guid.NewGuid().ToString("N")));Pump(debug);
            if(!debug.Result.Success)throw new Exception("Real debug metadata export failed: "+debug.Result.Message);
            var program=service.ExecuteAsync(new("program-export",Guid.NewGuid().ToString("N")));Pump(program);
            var hardware=service.ExecuteAsync(new("hardware-export",Guid.NewGuid().ToString("N")));Pump(hardware);
            var transfer=debug.Result.Transfer!.Value;
            var token=(string)Field("engineeringToken").GetValue(window)!;var runtimeToken=(string)Field("debugSessionToken").GetValue(window)!;var pin=(string)Field("engineeringCertificateFingerprint").GetValue(window)!;
            var socket=new DebugSocketClient(service.Origin,pin,engineeringToken:token);
            try
            {
                Pump(socket.ConnectAsync(runtimeToken));var identity=socket.GetProgramMd5Async();Pump(identity);var counts=socket.GetVariableCountsAsync();Pump(counts);
                if(identity.Result.Md5!=debug.Result.Active!.Value.GetProperty("program_md5").GetString())throw new Exception("Real debug program identity differs.");
            }
            finally{Pump(socket.DisposeAsync().AsTask());}
            var boundWithoutCandidate=false;
            LoadedProject? loaded=null;
            if(program.Result.Success)
            {
                var archive=program.Result.Transfer!.Value;
                loaded=EngineeringArchive.Read(JsonSerializer.Serialize(new{SchemaVersion=1,Project=archive.GetProperty("project"),Sources=archive.GetProperty("sources"),SourceHash=archive.GetProperty("source_hash").GetString(),HardwareHash=archive.GetProperty("hardware_hash").GetString()}));
            }
            else
            {
                foreach(var path in new[]{"examples","artifacts"}.SelectMany(directory=>Directory.EnumerateFiles(directory,"*.stproj",SearchOption.AllDirectories)).OrderByDescending(File.GetLastWriteTimeUtc).Take(300))
                {
                    try
                    {
                        var read=new ProjectStore().LoadAsync(path);Pump(read);var candidate=read.Result;var snapshot=ProjectValidator.Capture(candidate.Project,candidate.Sources);
                        if(snapshot.SourceHash.Equals(transfer.GetProperty("source_hash").GetString(),StringComparison.OrdinalIgnoreCase)&&snapshot.HardwareHash.Equals(transfer.GetProperty("hardware_hash").GetString(),StringComparison.OrdinalIgnoreCase)){loaded=candidate;break;}
                    }
                    catch(Exception e) when(e is InvalidDataException or IOException or InvalidOperationException){ }
                }
            }
            if(loaded is not null)
            {
                Field("project").SetValue(window,loaded.Project);Field("sources").SetValue(window,loaded.Sources.ToDictionary(s=>s.ItemId));Field("candidatePackage").SetValue(window,null);
                Pump(window.BindDebugAsync());
                if(Field("debugClient").GetValue(window) is null)throw new Exception("Engineering online disconnected without local candidate.");
                var end=DateTime.UtcNow.AddSeconds(30);
                while(DateTime.UtcNow<end){Pump(window.PollVariablesAsync());Dispatcher.UIThread.RunJobs();Thread.Sleep(200);}
                boundWithoutCandidate=Field("debugClient").GetValue(window) is not null;
            }
            var after=service.ExecuteAsync(new("status",Guid.NewGuid().ToString("N")));Pump(after);
            if(after.Result.State!=before.Result.State||after.Result.Active!.Value.GetProperty("build_hash").GetString()!=before.Result.Active!.Value.GetProperty("build_hash").GetString())throw new Exception("PLC state/program changed during read-only verification.");
            var evidence=new {Endpoint=service.Origin.ToString(),Paired=Environment.GetEnvironmentVariable("ST_ENGINEERING_PAIR_ONCE")=="1",State=after.Result.State,ActiveBuildHash=after.Result.Active!.Value.GetProperty("build_hash").GetString(),SourceHash=transfer.GetProperty("source_hash").GetString(),LeafCount=transfer.GetProperty("debug_map").GetProperty("leaves").GetArrayLength(),ProgramDebugMapVerified=true,ProgramExport=program.Result.Success,ProgramExportMessage=program.Result.Message,HardwareExport=hardware.Result.Success,HardwareExportMessage=hardware.Result.Message,OnlineWithoutCandidate=boundWithoutCandidate,ProgramAndStateUnchanged=true,Operations="session, status, program/debug/hardware export, readonly WebSocket monitor only; no stage/apply/start/stop/write"};
            File.WriteAllText("artifacts/scoped-engineering-device-verification.json",JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("PASS real engineering HTTPS session, active metadata, readonly WebSocket and unchanged PLC program/state");
        }
        finally{Field("allowClose").SetValue(window,true);window.Close();}
    }
}

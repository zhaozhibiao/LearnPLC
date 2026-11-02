using StWorkbench.Core;
using StWorkbench.Toolchain;
using System.IO.Compression;
using System.Text.Json;
internal static class TaskSchedulingPackageTests
{
 public static async Task RunAsync()
 {
  var a=new ProjectItem{Name="Main"};var b=new ProjectItem{Name="Fast",SourcePath="sources/Fast.st"};
  var p=new StProject{Items=[a,b],Tasks=[new(){Name="MainCycle",ProgramId=a.Id},new(){Name="FastCycle",ProgramId=b.Id,IntervalMilliseconds=5,Scheduling=PlcTaskScheduling.Realtime,Priority=3,Cpu=1}]};
  var docs=new SourceDocument[]{new(a.Id,a.SourcePath,"PROGRAM Main\nVAR Counter : DINT; END_VAR\nCounter := Counter + 1;\nEND_PROGRAM"),new(b.Id,b.SourcePath,"PROGRAM Fast\nVAR Value : DINT; END_VAR\nValue := Value + 1;\nEND_PROGRAM")};
  var snapshot=ProjectValidator.Capture(p,docs);
  var builder=new PackageBuilder();
  var result=await builder.BuildAsync(snapshot,p.Items.ToDictionary(i=>i.Id,i=>i.Name),"node",Path.GetFullPath("tools/compiler-adapter/compile.mjs"),Path.GetFullPath(".tools/strucpp-0.7.0/strucpp"),Path.GetFullPath("artifacts/task-scheduling-packages"));
  if(!result.Success)throw new Exception("Multi task package failed: "+result.Compilation.Log);
  using var zip=ZipFile.OpenRead(result.PackagePath);
  if(zip.GetEntry("task-scheduling.json") is null)throw new Exception("Task scheduling manifest missing");
  var cpp=File.ReadAllText(Path.Combine(result.Compilation.OutputDirectory,"generated.cpp"));
  if(!cpp.Contains("workbench_task_scheduling_v1"))throw new Exception("Native scheduling metadata missing");
  using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Compilation.OutputDirectory,"build-manifest.json")));
  if(manifest.RootElement.GetProperty("TaskSchedulingInterfaceVersion").GetString()!="workbench-task-scheduling-v1")throw new Exception("Scheduling capability missing");
  p.Tasks[1].Scheduling=PlcTaskScheduling.Ordinary;
  var changed=await builder.BuildAsync(ProjectValidator.Capture(p,docs),p.Items.ToDictionary(i=>i.Id,i=>i.Name),"node",Path.GetFullPath("tools/compiler-adapter/compile.mjs"),Path.GetFullPath(".tools/strucpp-0.7.0/strucpp"),Path.GetFullPath("artifacts/task-scheduling-packages"));
  if(!changed.Success || changed.BuildHash==result.BuildHash)throw new Exception("Scheduling change not bound into candidate identity");
  Console.WriteLine("PASS multi-task ordinary/realtime package, native metadata and scheduling identity");
 }
}

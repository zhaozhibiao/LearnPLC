using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StWorkbench.Core;
using StWorkbench.Mcp;

var root=Path.GetFullPath(args.Single());
var folder=Path.Combine(root,"examples","standard-axis-interfaces");
await using var client=await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions{
    Name="Standard MC interface example",Command="dotnet",Arguments=[Path.Combine(root,"src","StWorkbench.Mcp","bin","standard-interfaces-verify","StWorkbench.Mcp.dll"),"--config",Path.Combine(root,"artifacts","mcp-server.json")]}));
async Task<JsonElement> Call(string name,Dictionary<string,object?> arguments){
    var result=await client.CallToolAsync(name,arguments);
    var text=string.Join("\n",result.Content.OfType<TextContentBlock>().Select(c=>c.Text));
    if(result.IsError==true)throw new InvalidOperationException(name+": "+text);
    Console.WriteLine("MCP "+name+" OK");return JsonSerializer.Deserialize<JsonElement>(text);
}
var reference=await Call("project_open",new(){["projectPath"]="examples/library-axis-turn/LibraryAxis.stproj"});
var model=reference.GetProperty("Project").Deserialize<StProject>(McpJson.Options)!;
const string path="examples/standard-axis-interfaces/StandardInterfaces.stproj";
var exists=File.Exists(Path.Combine(root,path));
var view=await Call(exists?"project_open":"project_create",exists?new(){["projectPath"]=path}:new(){["projectPath"]=path,["name"]="标准工程轴接口 · 简洁往返"});
var session=view.GetProperty("SessionId").GetString()!;
string Revision()=>view.GetProperty("Revision").GetString()!;
var project=view.GetProperty("Project").Deserialize<StProject>(McpJson.Options)!;
view=await Call("configuration_update",new(){["sessionId"]=session,["expectedRevision"]=Revision(),["hardware"]=model.Hardware,["network"]=model.Network,["tasks"]=new[]{new PlcTask{ProgramId=project.Items.Single().Id,IntervalMilliseconds=10}}});
view=await Call("source_write",new(){["sessionId"]=session,["expectedRevision"]=Revision(),["itemId"]=project.Items.Single().Id,["text"]=File.ReadAllText(Path.Combine(folder,"Main.st"))});
view=await Call("project_save",new(){["sessionId"]=session,["expectedRevision"]=Revision()});
var operation=await Call("build_start",new(){["sessionId"]=session,["expectedRevision"]=Revision()});
var id=operation.GetProperty("OperationId").GetString()!;
while(operation.GetProperty("State").GetString() is "Queued" or "Running"){
    await Task.Delay(500);operation=await Call("build_status",new(){["operationId"]=id});
}
if(!operation.GetProperty("Success").GetBoolean())throw new InvalidOperationException("Compiler rejected standard interface example: "+operation);
var hash=operation.GetProperty("BuildHash").GetString()!;
var manifest=Directory.EnumerateFiles(Path.Combine(root,".st-mcp-builds"),"build-manifest.json",SearchOption.AllDirectories)
    .Where(p=>JsonDocument.Parse(File.ReadAllText(p)).RootElement.GetProperty("BuildHash").GetString()==hash).OrderByDescending(File.GetLastWriteTimeUtc).First();
var evidence=new{ProjectPath=Path.Combine(root,path),OutputDirectory=Path.GetDirectoryName(manifest),BuildHash=hash,SavedThroughMcp=true,CompiledThroughMcp=true,Downloaded=false,PhysicalMotionTested=false};
await File.WriteAllTextAsync(Path.Combine(folder,"build-evidence.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(evidence));

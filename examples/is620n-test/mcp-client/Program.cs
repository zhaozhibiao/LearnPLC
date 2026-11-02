using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StWorkbench.Core;
using StWorkbench.Mcp;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.CurrentDirectory).Replace(Path.DirectorySeparatorChar, '/');
var audit = new List<object>();
var transport = new StdioClientTransport(new StdioClientTransportOptions {
    Name = "IS620N engineering authoring", Command = "dotnet",
    Arguments = [root+"/artifacts/mcp-server/StWorkbench.Mcp.dll","--config",root+"/artifacts/mcp-server.json"] });
await using var client = await McpClient.CreateAsync(transport);
async Task<JsonElement> Call(string tool, Dictionary<string,object?> arguments)
{
    var result = await client.CallToolAsync(tool, arguments);
    var text = string.Join("\n",result.Content.OfType<TextContentBlock>().Select(c=>c.Text));
    audit.Add(new { tool, arguments, result=result.IsError==true ? "error" : "success", response=JsonSerializer.Deserialize<JsonElement>(text) });
    await File.WriteAllTextAsync(root+"/examples/is620n-test/mcp-evidence.json",JsonSerializer.Serialize(audit,new JsonSerializerOptions {WriteIndented=true}));
    if(result.IsError==true)throw new Exception(tool+": "+text);
    Console.WriteLine("MCP "+tool+" OK");return JsonSerializer.Deserialize<JsonElement>(text);
}
var tools=await client.ListToolsAsync();Console.WriteLine("Discovered "+tools.Count+" MCP tools");
_ = await Call("workbench_info",new());
const string path="examples/is620n-test/IS620N.stproj";
var view=await Call(File.Exists(root+"/"+path)?"project_open":"project_create", File.Exists(root+"/"+path)?new(){["projectPath"]=path}:new(){["name"]="IS620N 空载单次往返测试（待实机适配）",["projectPath"]=path});
var session=view.GetProperty("SessionId").GetString()!;
string Revision()=>view.GetProperty("Revision").GetString()!;
var model=view.GetProperty("Project").Deserialize<StProject>(McpJson.Options)!;
var entries=new PdoEntry[] {
 new(0x6040,0,16,"Controlword","UINT",false),new(0x607a,0,32,"TargetPosition","DINT",false),new(0x60ff,0,32,"TargetVelocity","DINT",false),new(0x6060,0,8,"ModeOfOperation","SINT",false),
 new(0x603f,0,16,"ErrorCode","UINT",true),new(0x6041,0,16,"Statusword","UINT",true),new(0x6064,0,32,"ActualPosition","DINT",true),new(0x606c,0,32,"ActualVelocity","DINT",true),new(0x6061,0,8,"ModeDisplay","SINT",true)};
var device=new EtherCatDevice(Guid.Parse("c5835b8d-873b-42d8-a74c-0546299c7ac5"),"IS620N_ECAT_v2.6.2 (commissioned 1600/1A00 PDO)",0x00100000,0x000c0108,0x00010001,entries);
var station=new EtherCatStation(Guid.Parse("0b9ac243-47e3-4d5e-b5ea-64f73dde5166"),"IS620NDrive",device.Id,0,entries);
var hardware=new HardwareConfiguration { Devices=[device],Stations=[station],Axes=[new("IS620N","Axis1","rev",-1,1){StationId=station.Id,MaximumVelocity=.01}] };
view=await Call("configuration_update",new(){["sessionId"]=session,["expectedRevision"]=Revision(),["hardware"]=hardware,["tasks"]=model.Tasks,["network"]=new NetworkConfiguration("192.168.1.31","a","","enp3s0")});
var item=view.GetProperty("Sources")[0].GetProperty("ItemId").GetGuid();
view=await Call("source_write",new(){["sessionId"]=session,["expectedRevision"]=Revision(),["itemId"]=item,["text"]=await File.ReadAllTextAsync(root+"/examples/is620n-test/Main.st")});
var check=await Call("project_validate",new(){["sessionId"]=session});
if(!check.GetProperty("success").GetBoolean())throw new Exception("Engineering validation failed: "+check);
view=await Call("project_save",new(){["sessionId"]=session,["expectedRevision"]=Revision()});
_ = await Call("motion_manifest",new(){["sessionId"]=session});
var build=await Call("build_start",new(){["sessionId"]=session,["expectedRevision"]=Revision()});
var id=build.GetProperty("OperationId").GetString()!;
do { await Task.Delay(200); build=await Call("build_status",new(){["operationId"]=id}); } while(build.GetProperty("State").GetString() is "Queued" or "Running");
if(!build.GetProperty("Success").GetBoolean())throw new Exception("Real compiler failed: "+build);
Console.WriteLine("MCP_PROJECT_AND_REAL_COMPILER_PASS "+build.GetProperty("BuildHash").GetString());

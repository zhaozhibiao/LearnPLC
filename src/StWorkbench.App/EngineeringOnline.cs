using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.OpenPlc;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private string? onlineBoundHardwareHash,onlineBoundBuildHash;
    private bool EngineeringOnlineIdentityReady()
    {
        if(engineeringClient is null||monitorSession is null||activeBuildHash!=onlineBoundBuildHash)return false;
        try {var snapshot=ProjectValidator.Capture(project,GetSources());return snapshot.SourceHash==monitorSession.Identity.SourceHash&&snapshot.HardwareHash==onlineBoundHardwareHash;}
        catch(InvalidDataException){return false;}
    }
    private async Task BindEngineeringDebugAsync()
    {
        if(deploymentBusy||debugSessionToken is null)throw new InvalidOperationException("请先连接工程服务并等待设备操作完成。");
        var service=engineeringClient!;var generation=connectionGeneration;
        var snapshot=ProjectValidator.Capture(project,GetSources());
        deploymentBusy=true;
        try
        {
            var response=await SendTransferRequestAsync(new(sshHost.Text??"",sshUser.Text??""),new("program-debug-map",Guid.NewGuid().ToString("N")));
            if(!response.Success)throw new InvalidOperationException(response.Message);
            if(response.Transfer is not {ValueKind:JsonValueKind.Object} transfer||response.Active is not {ValueKind:JsonValueKind.Object} active)throw new InvalidDataException("设备未返回活动程序调试身份。");
            if(!string.Equals(transfer.GetProperty("source_hash").GetString(),snapshot.SourceHash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("设备活动程序与本地源程序不一致，请上传程序或下载匹配程序。");
            if(!string.Equals(transfer.GetProperty("hardware_hash").GetString(),snapshot.HardwareHash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("本地工程组态与活动程序不一致，请核对或上传硬件组态。");
            var bytes=Encoding.UTF8.GetBytes(transfer.GetProperty("debug_map_json").GetString()!);
            if(bytes.Length>1024*1024)throw new InvalidDataException("调试映射超过容量。");
            var mapHash=Convert.ToHexString(SHA256.HashData(bytes));
            if(!mapHash.Equals(transfer.GetProperty("symbol_table_hash").GetString(),StringComparison.OrdinalIgnoreCase)||!mapHash.Equals(active.GetProperty("symbol_table_hash").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("调试符号身份不一致。");
            using var map=JsonDocument.Parse(bytes);var expectedMd5=map.RootElement.GetProperty("md5").GetString();
            if(expectedMd5!=active.GetProperty("program_md5").GetString())throw new InvalidDataException("调试映射与活动程序 MD5 不一致。");
            var symbols=map.RootElement.GetProperty("leaves").EnumerateArray().Select(leaf=>new DebugSymbol(leaf.GetProperty("path").GetString()!,leaf.GetProperty("type").GetString()!,new(leaf.GetProperty("arrayIdx").GetByte(),leaf.GetProperty("elemIdx").GetUInt16(),leaf.GetProperty("size").GetUInt16()))).ToArray();
            if(symbols.Length is <1 or >256||symbols.Sum(s=>(int)s.Address.ByteSize)>4086||symbols.Any(s=>string.IsNullOrWhiteSpace(s.Path)||string.IsNullOrWhiteSpace(s.Type))||symbols.Select(s=>s.Path).Distinct().Count()!=symbols.Length)throw new InvalidDataException("设备调试符号布局无效。");
            var client=new DebugSocketClient(service.Origin,engineeringCertificateFingerprint,engineeringToken:engineeringToken);
            try
            {
                await client.ConnectAsync(debugSessionToken);var identity=await client.GetProgramMd5Async();
                if(identity.Md5!=expectedMd5)throw new InvalidDataException("设备程序已切换，请重新绑定。");
                var counts=await client.GetVariableCountsAsync();
                if(symbols.Any(s=>s.Address.ArrayIndex>=counts.Count||s.Address.ElementIndex>=counts[s.Address.ArrayIndex]))throw new InvalidDataException("设备变量布局与调试符号不一致。");
                var current=ProjectValidator.Capture(project,GetSources());
                if(generation!=connectionGeneration||!ReferenceEquals(service,engineeringClient)||current.SourceHash!=snapshot.SourceHash||current.HardwareHash!=snapshot.HardwareHash)throw new InvalidOperationException("在线绑定期间工程或设备已改变。");
                activeBuildHash=active.GetProperty("build_hash").GetString();onlineBoundBuildHash=activeBuildHash;onlineBoundHardwareHash=snapshot.HardwareHash;
                debugClient=client;debugSymbols=symbols;debugLittleEndian=identity.LittleEndian;activeProgramMd5=expectedMd5;
                var session=new MonitorIdentity(snapshot.SourceHash,mapHash,Guid.NewGuid().ToString("N"));
                monitorSession=new(session,session,TimeSpan.FromSeconds(2));monitorSequence=0;trendPath=symbols[0].Path;
                await PollVariablesAsync();monitorTimer.Start();
            }
            catch {await client.DisposeAsync();throw;}
        }
        finally{deploymentBusy=false;}
    }
}

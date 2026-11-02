using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using StWorkbench.Core;
using StWorkbench.Toolchain;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private bool hardwareTransferScope;
    private Button uploadButton = null!;
    private void SetTransferScope(bool hardware)
    {
        hardwareTransferScope=hardware;
        if(downloadButton is not null) { downloadButton.Content=hardware?"下载硬件":"下载程序";downloadButton.IsEnabled=!busy&&!deploymentBusy; }
        if(uploadButton is not null) uploadButton.Content=hardware?"上传硬件":"上传程序";
    }
    private void SelectTransferScope(object? item)
    {
        SetTransferScope(item is TreeViewItem { Tag: EtherCatStation or "io-mappings" } || item is TreeViewItem { Tag: WorkbenchPageReference { TabIndex: 2 or 6 } });
    }
    private async Task DownloadSelectedAsync()
    {
        if(busy||deploymentBusy)return;
        if(hardwareTransferScope){await DownloadHardwareAsync();return;}
        var host=sshHost.Text;var user=sshUser.Text;var connection=connectionGeneration;
        await GeneratePackageAsync();
        if(host!=sshHost.Text||user!=sshUser.Text||connection!=connectionGeneration)throw new InvalidOperationException("编译期间连接目标已改变，请重新下载。");
        if(CandidateMatchesProject())await StageAndCompileAsync();
    }
    private async Task DownloadHardwareAsync()
    {
        if(busy||deploymentBusy) return;
        var captured=project;var generation=editGeneration;var connection=connectionGeneration;var target=new SshTarget(sshHost.Text??"",sshUser.Text??"");
        busy=deploymentBusy=true;downloadButton.IsEnabled=uploadButton.IsEnabled=false;
        string? baseline=null;
        bool Current()=>ReferenceEquals(project,captured)&&generation==editGeneration&&connection==connectionGeneration&&target.Host==sshHost.Text&&target.User==sshUser.Text;
        async Task<DeploymentResponse> Send(string action,string? build=null,string? bytes=null,string? hash=null)
        {
            if(!Current())throw new InvalidOperationException("硬件或设备目标已改变，请重新操作。");
            var request=new DeploymentRequest(action,Guid.NewGuid().ToString("N"),build,baseline,bytes,hash);
            var response=await SendTransferRequestAsync(target,request);
            if(!Current())throw new InvalidOperationException("硬件或设备目标已改变，请查询真实状态。");
            if(!response.Success)throw new InvalidOperationException(response.Message);
            return response;
        }
        try
        {
            var package=await new HardwarePackageBuilder().BuildAsync(project,Path.Combine(WorkbenchSettings.WorkspaceRoot,"artifacts","hardware-builds"),GetSources());
            if(!package.Success)throw new InvalidDataException(string.Join("\n",package.Errors));
            var bytes=await File.ReadAllBytesAsync(package.PackagePath);
            if(bytes.Length>32*1024*1024)throw new InvalidDataException("硬件包超过传输限制。");
            await Send("hardware-stage",package.BuildHash,Convert.ToBase64String(bytes),Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            var before=await Send("status");
            if(before.State is not ("running" or "stopped")||before.Active is not { ValueKind:JsonValueKind.Object } active)throw new InvalidOperationException("设备状态或活动程序基线未确认。");
            baseline=active.GetProperty("build_hash").GetString();
            await Send("hardware-validate",package.BuildHash);
            if(!await ConfirmAsync("下载硬件",$"目标：{target.User}@{target.Host}\n\n本次仅下载硬件、IO 和轴组态，不替换 PLC 程序。需要停止 PLC 并重启运动核心，所有轴必须已停稳且撤使能。\n\n完成后 PLC 保持停止；旧程序不匹配新组态时禁止启动。","下载硬件"))return;
            var confirmed=await Send("status");
            if(confirmed.State!=before.State||confirmed.Active?.GetProperty("build_hash").GetString()!=baseline)throw new InvalidOperationException("确认期间程序或运行状态已改变。");
            await DisconnectDebugAsync();
            if(before.State=="running")
            {
                var stopped=await Send("stop");
                if(stopped.State!="stopped"||stopped.Active?.GetProperty("build_hash").GetString()!=baseline)throw new InvalidOperationException("原程序停止状态未确认。");
            }
            var applied=await Send("hardware-apply",package.BuildHash);
            if(applied.State!="hardware-applied"||applied.Active?.GetProperty("build_hash").GetString()!=baseline)throw new InvalidOperationException("硬件应用结果未确认，请查询真实状态。");
            var final=await Send("status");
            if(final.State!="stopped"||final.Active?.GetProperty("build_hash").GetString()!=baseline)throw new InvalidOperationException("硬件下载后的程序身份和停止状态未确认。");
            ApplyDeploymentResponse(final,"status");compiledBuildHash=null;activateButton.IsEnabled=false;startButton.IsEnabled=false;
            buildStatus.Text="硬件下载完成 · PLC 保持停止，程序未替换";AppendLog(buildStatus.Text,false);
        }
        catch
        {
            remoteKnown=false;startButton.IsEnabled=stopButton.IsEnabled=false;buildStatus.Text="硬件下载未完成，请查询实际状态；不会自动重试";throw;
        }
        finally { busy=deploymentBusy=false;downloadButton.IsEnabled=uploadButton.IsEnabled=true; }
    }
    private async Task<DeploymentResponse> SendTransferRequestAsync(SshTarget target,DeploymentRequest request)
    {
        if(deploymentTransport is not null)return await deploymentTransport(target,request);
        if(engineeringClient is not null)
        {
            if(target.Host!=engineeringHost||target.User!=engineeringUser)throw new InvalidOperationException("工程服务目标与传输目标不一致。");
            var response=await engineeringClient.ExecuteAsync(request);
            if(response.State=="unauthorized")
            {
                await DisconnectDebugAsync();ResetEngineeringConnection();runtime?.Dispose();runtime=null;debugSessionToken=null;remoteKnown=false;startButton.IsEnabled=stopButton.IsEnabled=false;
            }
            return response;
        }
        if(!legacySshMode)throw new InvalidOperationException("请先连接常驻工程服务；未连接时不会自动回退到 SSH。");
        return await new RemoteDeploymentClient(knownHostsPath:Path.Combine(WorkbenchSettings.WorkspaceRoot,"artifacts","ssh-known-hosts")).ExecuteAsync(target,request);
    }
    private async Task UploadSelectedAsync()
    {
        if(busy||deploymentBusy)return;
        var hardware=hardwareTransferScope;var generation=editGeneration;var connection=connectionGeneration;var captured=project;var target=new SshTarget(sshHost.Text??"",sshUser.Text??"");
        bool Current()=>generation==editGeneration&&connection==connectionGeneration&&ReferenceEquals(project,captured)&&target.Host==sshHost.Text&&target.User==sshUser.Text;
        deploymentBusy=true;uploadButton.IsEnabled=false;
        try
        {
            var response=await SendTransferRequestAsync(target,new(hardware?"hardware-export":"program-export",Guid.NewGuid().ToString("N")));
            if(!response.Success)throw new InvalidOperationException(response.Message);
            if(!Current())throw new InvalidOperationException("上传期间工程或设备已变化，已保留当前工程。");
            if(response.Transfer is not { ValueKind:JsonValueKind.Object } transfer)throw new InvalidDataException("设备未返回可验证的工程内容。");
            var build=response.Active?.GetProperty("build_hash").GetString()??"未登记";
            var summary=hardware?"设备实际已应用的总线、IO 和轴组态":$"设备活动程序，共 {transfer.GetProperty("sources").GetArrayLength()} 个源文件、{transfer.GetProperty("project").GetProperty("Tasks").GetArrayLength()} 个任务";
            var consequence=hardware?"将用设备实际组态替换当前工程中的硬件、IO、轴、网络和通信配置。ST 源程序保留，设备不变；完成后可直接保存工程。":"上传为从 PLC 读回工程站，不改变设备。先保存为独立文件，当前编辑内容不会自动覆盖。";
            if(!await ConfirmAsync(hardware?"上传硬件":"上传程序",$"设备：{target.User}@{target.Host}\n活动程序：{build}\n内容：{summary}\n\n{consequence}",hardware?"更新当前工程":"保存上传内容"))return;
            if(!Current())throw new InvalidOperationException("确认期间工程或设备已变化，未覆盖当前工程。");
            if(hardware)
            {
                var configuration=transfer.GetProperty("configuration").Deserialize<HardwareConfigurationDocument>()??throw new InvalidDataException("硬件组态为空。");
                if(configuration.SchemaVersion!=1||configuration.State!="offline-engineering"||configuration.Hardware is null)throw new InvalidDataException("设备硬件组态格式无效。");
                var old=project.Hardware;var oldNetwork=project.Network;var oldCycle=project.MotionCycle;var oldCommunication=project.Communication;
                project.Hardware=configuration.Hardware;project.Network=configuration.Network;project.MotionCycle=configuration.MotionCycle;project.Communication=configuration.Communication;
                var errors=ProjectValidator.Validate(project,GetSources());
                if(errors.Count>0){project.Hardware=old;project.Network=oldNetwork;project.MotionCycle=oldCycle;project.Communication=oldCommunication;throw new InvalidDataException(string.Join("\n",errors.Select(e=>e.Message)));}
                MarkDirty();RefreshHardware();RestoreNetworkConfiguration();RefreshCommunication();SetTransferScope(true);
                AppendLog("设备实际硬件组态已上传到当前工程，请保存工程。",false);
            }
            else
            {
                var loaded=EngineeringArchive.Read(JsonSerializer.Serialize(new {SchemaVersion=1,Project=transfer.GetProperty("project"),Sources=transfer.GetProperty("sources"),SourceHash=transfer.GetProperty("source_hash").GetString(),HardwareHash=transfer.GetProperty("hardware_hash").GetString()}));
                var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions{Title="上传程序：保存为独立工程",SuggestedFileName=loaded.Project.Name+"-PLC.stproj",DefaultExtension="stproj"});
                if(file is null)return;
                if(!Current())throw new InvalidOperationException("保存期间工程或设备已变化，未写入上传内容。");
                var path=file.TryGetLocalPath()??throw new InvalidDataException("请选择本地工程保存位置。");
                if(!path.EndsWith(".stproj",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请保存为独立的 .stproj 工程文件。");
                if(projectPath is not null&&Path.GetFullPath(path).Equals(Path.GetFullPath(projectPath),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请保存为独立工程文件，以保留当前编辑内容。");
                await store.SaveAsync(path,loaded.Project,loaded.Sources);
                AppendLog("设备活动程序及 ST 源工程已上传并保存："+path,false);
                if(await ConfirmAsync("打开上传程序","程序已保存为独立工程。是否打开？当前未保存的修改会再次确认。","打开上传工程")&&await ConfirmDiscardAsync())
                {
                    if(!Current())throw new InvalidOperationException("工程或设备已改变，未覆盖当前编辑内容。");
                    await LoadAtAsync(path);
                }
            }
        }
        finally {deploymentBusy=false;uploadButton.IsEnabled=true;downloadButton.IsEnabled=!busy;}
    }
}

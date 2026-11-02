using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using StWorkbench.OpenPlc;
using StWorkbench.Toolchain;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private EngineeringClient? engineeringClient;
    private string? engineeringToken;
    private string? engineeringHost, engineeringUser;
    private string? engineeringCertificateFingerprint;
    private bool legacySshMode;
    private async Task PairEngineeringAsync()
    {
        if (connecting || busy || deploymentBusy) return;
        var host=sshHost.Text??""; var user=sshUser.Text??"";
        if (!Regex.IsMatch(host,@"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") || !Regex.IsMatch(user,@"^[A-Za-z_][A-Za-z0-9_-]{0,31}$")) throw new InvalidDataException("设备地址或用户无效。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("ssh") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
        foreach(var argument in new[]{"-o","BatchMode=yes","-o","StrictHostKeyChecking=yes","-o","ConnectTimeout=8","-o","UserKnownHostsFile="+Path.Combine(WorkbenchSettings.WorkspaceRoot,"artifacts","ssh-known-hosts"),"--",user+"@"+host,"/home/a/st-workbench-runtime-install/st-session-helper --pair "+host}) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法建立首次配对通道。");
        async Task<string> ReadBounded(StreamReader reader)
        {
            var result=new System.Text.StringBuilder(); var chars=new char[1024];int length;
            while((length=await reader.ReadAsync(chars.AsMemory(),deadline.Token))>0) { if(result.Length+length>16384) throw new InvalidDataException("设备配对响应过大。");result.Append(chars,0,length); }
            return result.ToString();
        }
        var output=ReadBounded(process.StandardOutput);var errors=ReadBounded(process.StandardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token); var json=await output; _=await errors;
            if(process.ExitCode!=0) throw new InvalidOperationException("首次设备配对失败，请检查工程服务安装和已配置的 SSH 密钥。");
            using var document=JsonDocument.Parse(json); var root=document.RootElement;
            var value=new EngineeringCredentials { Endpoint=root.GetProperty("endpoint").GetString()!,Token=root.GetProperty("token").GetString()!,CertificateSha256=root.GetProperty("certificate_sha256").GetString()! };
            if(!Uri.TryCreate(value.Endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!=host||uri.Port!=18445||value.CertificateSha256.Length!=64||!value.CertificateSha256.All(Uri.IsHexDigit)||value.Token.Length is <32 or >8192||value.Token.Any(c=>c<33||c>126)) throw new InvalidDataException("设备配对身份无效。");
            if(host!=sshHost.Text||user!=sshUser.Text) throw new InvalidOperationException("配对期间设备目标已改变。");
            EngineeringCredentialStore.Save(host,user,value);
            AppendLog("工程服务已配对。后续连接使用已保存的设备授权，无需 SSH。",false);
        }
        finally
        {
            if(!process.HasExited) process.Kill(true); deadline.Cancel();
            foreach(var pending in new[]{output,errors}) try { await pending; } catch(Exception) { }
        }
        await ConnectEngineeringAsync();
    }
    public async Task ConnectEngineeringAsync()
    {
        if(connecting) return;
        var host=sshHost.Text??"";var user=sshUser.Text??"";
        var credentials=EngineeringCredentialStore.Load(host,user) ?? throw new InvalidOperationException("设备尚未配对，请点击“首次配对工程服务”；日常在线不需要 SSH。");
        connecting=true;var generation=++connectionGeneration;
        remoteKnown=false;startButton.IsEnabled=stopButton.IsEnabled=false;
        EngineeringClient? next=null;
        try
        {
            await DisconnectDebugAsync(); runtime?.Dispose();runtime=null;debugSessionToken=null;engineeringClient?.Dispose();engineeringClient=null;
            privateTunnel?.Dispose();privateTunnel=null;
            next=new EngineeringClient(new Uri(credentials.Endpoint),credentials.Token,credentials.CertificateSha256);
            var token=await next.OpenSessionAsync();
            if(generation!=connectionGeneration||host!=sshHost.Text||user!=sshUser.Text) throw new InvalidOperationException("连接期间设备目标已改变。");
            var nextRuntime=new OpenPlcClient(next.Origin,certificateFingerprint:credentials.CertificateSha256,engineeringToken:credentials.Token);nextRuntime.UseSessionToken(token);
            engineeringClient=next; next=null;engineeringToken=credentials.Token;engineeringHost=host;engineeringUser=user;
            engineeringCertificateFingerprint=credentials.CertificateSha256; privateRuntimeEndpoint=new Uri(credentials.Endpoint); runtime=nextRuntime;debugSessionToken=token;
            await RefreshRuntimeAsync();await RefreshDeploymentStatusAsync();
            AppendLog("常驻工程服务已连接，在线和工程传输使用 HTTPS 会话。",false);
        }
        catch
        {
            ResetEngineeringConnection();runtime?.Dispose();runtime=null;debugSessionToken=null;privateRuntimeEndpoint=null;refreshButton.IsEnabled=false;
            remoteKnown=false;startButton.IsEnabled=stopButton.IsEnabled=false;runtimeDetails.Text="工程服务连接未确认，请检查设备网络与配对授权。";throw;
        }
        finally { next?.Dispose();connecting=false;RefreshNetworkTopology(); }
    }
    private void ResetEngineeringConnection()
    {
        engineeringClient?.Dispose();engineeringClient=null;engineeringToken=null;engineeringCertificateFingerprint=null;engineeringHost=engineeringUser=null;legacySshMode=false;
    }
}

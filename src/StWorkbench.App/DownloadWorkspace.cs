using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StWorkbench.Toolchain;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private sealed class ProgramApplicationException(string message, Exception inner) : InvalidOperationException(message, inner);
    private async Task DownloadAndApplyAsync(bool upload)
    {
        if (deploymentBusy || busy) return;
        if (!CandidateMatchesProject()) throw new InvalidOperationException("工程已变更，请重新生成程序包。");
        var package = candidatePackage!;
        if (!upload && compiledBuildHash != package.BuildHash) throw new InvalidOperationException("需要当前工程的 Linux 编译通过程序。");
        var capturedProject = project;
        var capturedGeneration = connectionGeneration;
        var target = new SshTarget(sshHost.Text ?? "", sshUser.Text ?? "");
        bool IsCurrent()
        {
            if (!ReferenceEquals(project, capturedProject) || !ReferenceEquals(candidatePackage, package) || connectionGeneration != capturedGeneration || target.Host != sshHost.Text || target.User != sshUser.Text) return false;
            try { return CandidateMatchesProject(); } catch (InvalidDataException) { return false; }
        }
        var host = new DownloadHost(this, IsCurrent, target);
        deploymentBusy = busy = true; operationTarget = target; downloadButton.IsEnabled = activateButton.IsEnabled = false;
        try
        {
            ProgramDownloadOutcome outcome;
            if (upload)
            {
                compiledBuildHash = null; motionDeploymentReady = false;
                var bytes = await File.ReadAllBytesAsync(package.PackagePath);
                if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("程序包超过上传限制。");
                var request = new ProgramDownloadRequest(package.BuildHash, Convert.ToBase64String(bytes), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                outcome = await ProgramDownloadWorkflow.ExecuteAsync(request, host);
            }
            else outcome = await ProgramDownloadWorkflow.ApplyCompiledAsync(package.BuildHash, host);
            switch (outcome.State)
            {
                case ProgramDownloadState.Activated:
                    var resumed = outcome.Status?.State == "running";
                    buildStatus.Text = resumed ? "下载完成 · 程序已应用，已恢复运行" : "下载完成 · 程序已应用，已停止，保持原停止状态";
                    AppendLog(resumed ? "新程序已应用并核对为运行状态，已恢复原运行状态。" : "新程序已应用并核对为停止状态，保持原停止状态。", false);
                    break;
                case ProgramDownloadState.AlreadyActive:
                    buildStatus.Text = outcome.Status?.State == "running" ? "该程序已在 PLC 中 · 运行中，未重启核心" : "该程序已在 PLC 中 · 已停止，未重启核心";
                    break;
                case ProgramDownloadState.Cancelled:
                    buildStatus.Text = "Linux 编译通过 · 已取消应用，未执行程序切换";
                    break;
                case ProgramDownloadState.Blocked:
                    break;
            }
        }
        catch (Exception error)
        {
            remoteKnown = false; startButton.IsEnabled = stopButton.IsEnabled = activateButton.IsEnabled = false;
            deviceRunStatus.Text = "设备状态未确认，请刷新";
            buildStatus.Text = "下载 / 应用未完成 · 请刷新真实设备状态后处理，不会自动重试";
            throw new ProgramApplicationException(error.Message, error);
        }
        finally
        {
            deploymentBusy = busy = false; operationTarget = null;
            var current = IsCurrent();
            downloadButton.IsEnabled = current;
            RefreshCompiledProgramAction();
        }
    }

    private void RefreshCompiledProgramAction()
    {
        activateButton.IsEnabled = false;
        if (busy || deploymentBusy || !remoteKnown || candidatePackage is null || compiledBuildHash != candidatePackage.BuildHash || !motionDeploymentReady ||
            project.Hardware.Axes.Any(a => a.Mechanics is not null) || string.Equals(activeBuildHash, candidatePackage.BuildHash, StringComparison.OrdinalIgnoreCase)) return;
        try { activateButton.IsEnabled = CandidateMatchesProject(); } catch (InvalidDataException) { }
    }

    private sealed class DownloadHost(MainWindow owner, Func<bool> isCurrent, SshTarget target) : IProgramDownloadHost
    {
        public bool IsCurrent => isCurrent();
        public bool CanApply => IsCurrent && owner.candidatePackage is not null && owner.compiledBuildHash == owner.candidatePackage.BuildHash &&
            owner.motionDeploymentReady && owner.project.Hardware.Axes.All(a => a.Mechanics is null);
        public async Task<DeploymentResponse> SendAsync(string action, string? packageBase64 = null, string? packageHash = null)
        {
            owner.buildStatus.Text = action switch
            {
                "stage" => "正在上传程序…", "compile" => "Linux 编译中…", "stop" => "正在停止原程序…",
                "activate" => "正在重启 Runtime 核心并应用程序…", "start" => "正在恢复新程序运行…", _ => owner.buildStatus.Text
            };
            return await owner.SendDeploymentAsync(action, packageBase64, packageHash);
        }
        public void ApplyCompilation(DeploymentResponse result) => owner.ApplyCompiledCandidate(result);
        public Task<bool> ConfirmRestartAsync(ProgramRestartNotice notice) => owner.ConfirmProgramRestartAsync(notice, target);
        public async Task BeforeProgramChangeAsync()
        {
            owner.runtimeDetails.Text = "程序即将切换，Runtime 核心会重启；请在下载完成后重新连接在线监视。";
            await owner.DisconnectDebugAsync();
        }
    }

    private async Task<bool> ConfirmProgramRestartAsync(ProgramRestartNotice notice, SshTarget target)
    {
        var dialog = new Window { Title = "下载并应用程序", Width = 560, Height = 330, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var state = notice.WasRunning ? "当前 PLC 程序正在运行，继续后将先停止原程序。" : "当前 PLC 程序已停止。";
        var completion = notice.WasRunning ? "应用完成并核对新程序后自动恢复运行。" : "应用完成后保持停止，不会自动运行。";
        var message = $"目标：{target.User}@{target.Host}\n\n{state}\n本次应用需要重启 Runtime 核心，在线监视会短暂断开。\n\n{completion}取消将保留已编译程序，不执行停止或切换。";
        var yes = new Button { Content = "重启核心并应用" }; var no = new Button { Content = "取消" };
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(22), Spacing = 18, Children =
        {
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } }
        } };
        return await dialog.ShowDialog<bool>(this);
    }
}

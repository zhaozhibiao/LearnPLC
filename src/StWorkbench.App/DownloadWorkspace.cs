using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StWorkbench.Toolchain;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private DeploymentResponse? compiledReadiness;
    private bool configurationPreparationReady;
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
            downloadButton.IsEnabled = true;
            RefreshCompiledProgramAction();
        }
    }

    private void RefreshCompiledProgramAction()
    {
        activateButton.IsEnabled = false;
        if (busy || deploymentBusy || !remoteKnown || candidatePackage is null || compiledBuildHash != candidatePackage.BuildHash || !(motionDeploymentReady || configurationPreparationReady) ||
            HasOfflineAxisMechanics || (string.Equals(activeBuildHash, candidatePackage.BuildHash, StringComparison.OrdinalIgnoreCase) && !configurationPreparationReady)) return;
        try { activateButton.IsEnabled = CandidateMatchesProject(); } catch (InvalidDataException) { }
    }

    private sealed class DownloadHost(MainWindow owner, Func<bool> isCurrent, SshTarget target) : IProgramDownloadHost
    {
        public bool IsCurrent => isCurrent();
        public bool CanApply => IsCurrent && owner.candidatePackage is not null && owner.compiledBuildHash == owner.candidatePackage.BuildHash &&
            owner.motionDeploymentReady && !owner.HasOfflineAxisMechanics;
        public bool RequiresConfigurationCheck => owner.project.Hardware.Runtime is not null;
        public bool CanApplyAfterConfiguration => IsCurrent && owner.candidatePackage is not null && owner.compiledBuildHash == owner.candidatePackage.BuildHash && owner.configurationPreparationReady;
        public async Task<DeploymentResponse> SendAsync(string action, string? packageBase64 = null, string? packageHash = null)
        {
            owner.buildStatus.Text = action switch
            {
                "stage" => "正在上传程序…", "compile" => "Linux 编译中…", "stop" => "正在停止原程序…",
                "activate" => "正在重启 Runtime 核心并应用程序…", "start" => "正在恢复新程序运行…", _ => owner.buildStatus.Text
            };
            if (action == "configuration-plan") owner.buildStatus.Text = "正在比较程序所需组态与设备组态…";
            return await owner.SendDeploymentAsync(action, packageBase64, packageHash);
        }
        public void ReportConfigurationMismatch(ProgramConfigurationPlan plan)
        {
            owner.AppendLog(plan.Kind == "axis" ? "轴映射、限值或周期与设备不一致，请先下载硬件；未停止原程序，未修改组态。" : "物理总线、IO 或轴参数与设备不一致，请先下载硬件；未停止原程序，未修改组态。", true);
            owner.buildStatus.Text = "程序下载已停止 · 硬件组态不一致，请先选择左侧硬件并下载硬件";
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
        var dialog = new Window { Title = "下载并应用程序", Width = 590, Height = 430, MinWidth = 420, MinHeight = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true };
        var state = notice.WasRunning ? "当前 PLC 程序正在运行，继续后将先停止原程序。" : "当前 PLC 程序已停止。";
        var completion = notice.WasRunning ? "应用完成并核对新程序后自动恢复运行。" : "应用完成后保持停止，不会自动运行。";
        var configuration = notice.ConfigurationChange switch
        {
            "hardware" => "\n本次同时更新总线／IO／轴参数组态，需要重启运动核心并重新配置从站。所有轴必须已停稳且撤使能，否则将停止此次应用。",
            "axis" => "\n本次同时更新轴服务的映射、限值或 PLC 周期。所有轴必须已停稳且撤使能；更新轴服务会短暂中断轴反馈，运动核心保持运行。",
            _ => "\n设备组态无需更新。"
        };
        var message = $"目标：{target.User}@{target.Host}\n\n{state}\n本次应用需要重启 Runtime 核心，在线监视会短暂断开。{configuration}\n\n{completion}\n取消将保留已编译程序，不更新组态、不执行停止或切换。";
        var yes = new Button { Content = "重启核心并应用" }; var no = new Button { Content = "取消" };
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        var content = new Grid { Margin = new Thickness(22), RowDefinitions = new("*,Auto"), RowSpacing = 18 };
        content.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } };
        Grid.SetRow(footer, 1); content.Children.Add(footer); dialog.Content = content;
        return await dialog.ShowDialog<bool>(this);
    }
}

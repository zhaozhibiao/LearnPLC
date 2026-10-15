using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StWorkbench.Core;
using StWorkbench.Toolchain;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private readonly CheckBox motionCycleEnabled = new() { Content = "配置独立轴控周期（不自动启动运动）" };
    private readonly TextBox motionCyclePeriod = new() { Text = "1000", Width = 140 };
    private readonly TextBox motionCyclePriority = new() { Text = "40", Width = 140 };
    private readonly TextBox motionCycleCpu = new() { Text = "", PlaceholderText = "留空：不绑定", Width = 140 };
    private readonly TextBox motionCycleWatchdog = new() { Text = "3", Width = 140 };
    private readonly ListBox motionCycleAxes = new() { SelectionMode = SelectionMode.Multiple, Height = 110 };
    private readonly TextBlock motionCycleStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#7ECED4") };
    private readonly TextBox motionRealtimeStatus = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 150, MaxHeight = 230, Text = "设备实时状态未核验。" };

    private Control MotionCyclePane()
    {
        Register(motionCycleEnabled, "MotionCycleEnabled"); Register(motionCyclePeriod, "MotionCyclePeriod"); Register(motionCyclePriority, "MotionCyclePriority"); Register(motionCycleCpu, "MotionCycleCpu"); Register(motionCycleWatchdog, "MotionCycleWatchdog"); Register(motionCycleAxes, "MotionCycleAxes"); Register(motionCycleStatus, "MotionCycleStatus"); Register(motionRealtimeStatus, "MotionRealtimeStatus");
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 15, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "独立轴控周期", FontSize = 22, FontWeight = FontWeight.Bold });
        panel.Children.Add(Label("用户 ST 工艺程序继续使用上方 PLC 周期。轴控周期由自有运动核心执行，不绑定用户程序。"));
        panel.Children.Add(motionCycleEnabled);
        var fields = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var pair in new[] { ("周期（μs）", motionCyclePeriod), ("FIFO 优先级", motionCyclePriority), ("CPU 编号", motionCycleCpu), ("连续超时周期数", motionCycleWatchdog) })
            fields.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 14, 0), Children = { Label(pair.Item1), pair.Item2 } });
        panel.Children.Add(fields); panel.Children.Add(Label("轴组：按 Ctrl / Shift 选择多个工程轴；轴引用在“硬件与轴”中维护。")); panel.Children.Add(motionCycleAxes);
        var apply = ActionButton("应用轴控周期到工程", () =>
        {
            if (busy) throw new InvalidOperationException("当前工程操作尚未结束。");
            if (motionCycleEnabled.IsChecked != true) { ApplyMotionCycleConfiguration((project.MotionCycle ?? new()) with { Enabled = false }); return Task.CompletedTask; }
            int Number(TextBox box, string field) => int.TryParse(box.Text, out var n) ? n : throw new InvalidDataException(field + "必须填写整数。");
            ApplyMotionCycleConfiguration(new()
            {
                Enabled = true, PeriodMicroseconds = Number(motionCyclePeriod, "轴控周期"), Priority = Number(motionCyclePriority, "优先级"),
                Cpu = string.IsNullOrWhiteSpace(motionCycleCpu.Text) ? null : Number(motionCycleCpu, "CPU 编号"), WatchdogCycles = Number(motionCycleWatchdog, "超时周期数"),
                AxisNames = motionCycleAxes.SelectedItems?.OfType<string>().ToArray() ?? []
            });
            return Task.CompletedTask;
        });
        Register(apply, "ApplyMotionCycle"); panel.Children.Add(apply); panel.Children.Add(motionCycleStatus);
        var refresh = ActionButton("核验设备实时状态（只读）", RefreshMotionRealtimeAsync); Register(refresh, "RefreshMotionRealtime"); panel.Children.Add(refresh); panel.Children.Add(motionRealtimeStatus);
        panel.Children.Add(Label("这些参数随候选包保存。填写周期或启用配置不代表设备已生效；真实执行适配未接通时，相关候选不可激活。"));
        return panel;
    }

    private void RefreshMotionCycle()
    {
        var config = project.MotionCycle ?? new();
        motionCycleEnabled.IsChecked = config.Enabled;
        motionCyclePeriod.Text = config.PeriodMicroseconds.ToString(); motionCyclePriority.Text = config.Priority.ToString(); motionCycleCpu.Text = config.Cpu?.ToString() ?? ""; motionCycleWatchdog.Text = config.WatchdogCycles.ToString();
        motionCycleAxes.ItemsSource = project.Hardware.Axes.Select(a => a.Name).ToArray();
        motionCycleAxes.SelectedItems = config.AxisNames.Where(n => project.Hardware.Axes.Any(a => a.Name == n)).ToList();
        motionCycleStatus.Text = config.Enabled ? "工程轴控周期已配置 · 设备未生效；真实执行适配未接通。" : "工程轴控周期未启用；用户程序仍使用 PLC 周期。";
    }

    public void ApplyMotionCycleConfiguration(MotionCycleConfiguration config)
    {
        if (busy) throw new InvalidOperationException("当前工程操作尚未结束。");
        var previous = project.MotionCycle;
        project.MotionCycle = config with { AxisNames = config.AxisNames?.ToArray() ?? [] };
        var errors = ProjectValidator.Validate(project, GetSources());
        if (errors.Count > 0) { project.MotionCycle = previous; throw new InvalidDataException(string.Join("\n", errors.Select(e => e.Message))); }
        MarkDirty(); RefreshMotionCycle(); motionRealtimeStatus.Text = "工程配置已变更，设备实时状态未核验；配置尚未生效。";
    }

    private Task RefreshMotionRealtimeAsync() => RefreshMotionRealtimeCoreAsync((target, request) => new RemoteDeploymentClient(knownHostsPath: Path.Combine(WorkbenchSettings.WorkspaceRoot, "artifacts", "ssh-known-hosts")).ExecuteAsync(target, request));

    private async Task RefreshMotionRealtimeCoreAsync(Func<SshTarget, DeploymentRequest, Task<DeploymentResponse>> read)
    {
        if (busy || deploymentBusy) return;
        var target = new SshTarget(sshHost.Text ?? "", sshUser.Text ?? ""); var generation = connectionGeneration;
        var inspectedProject = project; var inspectedEdit = editGeneration;
        bool Current() => generation == connectionGeneration && ReferenceEquals(project, inspectedProject) && editGeneration == inspectedEdit && target.Host == sshHost.Text && target.User == sshUser.Text;
        deploymentBusy = true; motionRealtimeStatus.Text = "正在只读核验设备内核与实际线程调度…";
        try
        {
            var response = await read(target, new("realtime-status", Guid.NewGuid().ToString("N")));
            if (!Current()) return;
            if (!response.Success || response.State != "diagnostic" || response.Realtime is not { ValueKind: JsonValueKind.Object } report) throw new InvalidOperationException(response.Message.Length > 0 ? response.Message : "设备没有返回可验证的实时状态。");
            ApplyMotionRealtimeReport(report);
        }
        catch (Exception e) { if (!Current()) return; motionRealtimeStatus.Text = "实时状态未核验 · " + e.Message; throw; }
        finally { deploymentBusy = false; }
    }

    private void ApplyMotionRealtimeReport(JsonElement report)
    {
        string Text(JsonElement value, string field) => value.TryGetProperty(field, out var found) ? found.ToString() : "未知";
        var lines = new List<string> { "设备内核：" + Text(report, "KernelRelease") };
        lines.Add("实时内核：" + (report.TryGetProperty("RealtimeKernel", out var rt) && rt.ValueKind is JsonValueKind.True or JsonValueKind.False ? rt.GetBoolean() ? "已启用 PREEMPT_RT" : "未启用" : "未知"));
        lines.Add("CPU 数量：" + Text(report, "CpuCount"));
        void Threads(JsonElement array, string prefix)
        {
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0) { lines.Add(prefix + "：没有可核验线程"); return; }
            foreach (var t in array.EnumerateArray().Take(256))
            {
                var cpus = t.TryGetProperty("Cpus", out var cpuArray) && cpuArray.ValueKind == JsonValueKind.Array ? string.Join(",", cpuArray.EnumerateArray().Select(c => c.ToString())) : "未知";
                lines.Add(prefix + " " + Text(t, "Name") + "：" + Text(t, "Policy") + "，优先级 " + Text(t, "Priority") + "，CPU " + cpus);
            }
        }
        if (report.TryGetProperty("PlcThreads", out var plc))
        {
            var cyclic = plc.ValueKind == JsonValueKind.Array ? plc.EnumerateArray().Where(t => Text(t, "Name").StartsWith("TASK_", StringComparison.Ordinal)).ToArray() : [];
            if (cyclic.Length > 0) Threads(JsonSerializer.SerializeToElement(cyclic), "PLC 周期线程"); else Threads(plc, "PLC");
        }
        if (report.TryGetProperty("MotionService", out var motion))
        {
            lines.Add("运动服务 " + Text(motion, "Name") + "：" + Text(motion, "State"));
            if (motion.TryGetProperty("Threads", out var threads)) Threads(threads, "轴控");
        }
        lines.Add("实际轴控周期 / 最大抖动 / 超时次数：不可用，执行核心统计接口尚未接入。");
        lines.Add("以上为只读核验结果，不表示工程配置已生效。" );
        if (project.MotionCycle?.Cpu is int cpu && report.TryGetProperty("CpuCount", out var count) && count.TryGetInt32(out var n) && cpu >= n) lines.Add("工程配置 CPU 编号超出设备核心数量，不能应用。");
        motionRealtimeStatus.Text = string.Join("\n", lines);
    }
}

using Avalonia.Controls;
using Avalonia.Layout;
using StWorkbench.Core;
using StWorkbench.OpenPlc;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private sealed record OnlineBooleanChoice(string Display, DebugSymbol Symbol)
    {
        public override string ToString() => Display;
    }
    private readonly ComboBox onlineWriteSymbol = new() { MinWidth = 270, PlaceholderText = "选择用户 BOOL 参数" };
    private readonly ComboBox onlineWriteValue = new() { ItemsSource = new[] { "TRUE", "FALSE" }, SelectedIndex = 1, MinWidth = 100 };
    private readonly TextBlock onlineWriteCurrent = new() { Text = "当前值：不可用" };
    private readonly TextBlock onlineWriteStatus = new() { Text = "未绑定：不能写入。", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private Button onlineWriteOnce = null!;
    private bool onlineWriteBusy, refreshingOnlineWrite;
    private (ReadOnlyMonitorSession Session, string Path, bool Value, long Sequence)? pendingOnlineWrite;
    private Func<string, string, string, Task<bool>>? onlineWriteConfirmation = null;
    private Func<DebugSocketClient, string, DebugVariableAddress, bool, Task>? onlineBooleanWriter = null;

    private Control OnlineWritePane()
    {
        Register(onlineWriteSymbol, "OnlineWriteSymbol"); Register(onlineWriteValue, "OnlineWriteValue");
        Register(onlineWriteCurrent, "OnlineWriteCurrent"); Register(onlineWriteStatus, "OnlineWriteStatus");
        onlineWriteOnce = ActionButton("写入一次", WriteOnlineBooleanAsync); Register(onlineWriteOnce, "OnlineWriteOnce");
        onlineWriteOnce.IsEnabled = false;
        onlineWriteSymbol.SelectionChanged += (_, _) => { if (!refreshingOnlineWrite) RefreshOnlineWrite(); };
        onlineWriteValue.SelectionChanged += (_, _) => { if (!refreshingOnlineWrite) RefreshOnlineWrite(); };
        return new StackPanel { Spacing = 8, Children =
        {
            Label("修改 BOOL 变量 · 确认后写入一次，程序仍可覆盖"),
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { onlineWriteSymbol, onlineWriteValue, onlineWriteOnce } },
            onlineWriteCurrent, onlineWriteStatus
        } };
    }

    private IReadOnlyList<OnlineBooleanChoice> EligibleOnlineBooleans()
    {
        var indexed = language.Index(GetSources());
        var program = project.Items.FirstOrDefault(i => i.Id == project.Tasks.FirstOrDefault(t => t.Enabled)?.ProgramId)?.Name;
        var programs = indexed.Where(s => s.Kind == "PROGRAM").Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<OnlineBooleanChoice>();
        foreach (var symbol in debugSymbols)
        {
            var parts = symbol.Path.Split('.');
            if (parts.Length != 2 || symbol.Address.ByteSize != 1 || !symbol.Type.Equals("BOOL", StringComparison.OrdinalIgnoreCase) || parts.Any(p => p.StartsWith("AB_", StringComparison.OrdinalIgnoreCase))) continue;
            var owner = parts[0].Equals("INSTANCE0", StringComparison.OrdinalIgnoreCase) ? program : parts[0];
            if (owner is null || !programs.Contains(owner)) continue;
            var declaration = indexed.FirstOrDefault(s => s.Owner?.Equals(owner,StringComparison.OrdinalIgnoreCase) == true &&
                s.Name.Equals(parts[1],StringComparison.OrdinalIgnoreCase) && s.Type.Equals("BOOL",StringComparison.OrdinalIgnoreCase) && s.Kind == "VAR");
            if (declaration is not null) result.Add(new(owner + "." + declaration.Name, symbol));
        }
        return result;
    }

    private bool OnlineWriteIdentityReady() => !deploymentBusy && !busy && !bindingDebug &&
        debugClient is { IsConnected: true } && !string.IsNullOrWhiteSpace(debugSessionToken) && monitorSession is not null &&
        activeProgramMd5 is { Length: 32 } && (EngineeringOnlineIdentityReady() ||
        CandidateMatchesProject() && candidatePackage!.BuildHash == activeBuildHash && monitorSession.Identity.SourceHash == candidateSourceHash &&
        monitorSession.Identity.SymbolHash.Equals(candidatePackage.SymbolTableHash, StringComparison.OrdinalIgnoreCase));

    private bool OnlineWriteSelectionReady(OnlineBooleanChoice choice) => OnlineWriteIdentityReady() &&
        EligibleOnlineBooleans().Any(c => c == choice) &&
        monitorSession!.GetSample(choice.Symbol.Path, DateTimeOffset.UtcNow).Quality == SampleQuality.Good;

    private void RefreshOnlineWrite()
    {
        if (onlineWriteOnce is null || refreshingOnlineWrite) return;
        refreshingOnlineWrite = true;
        try
        {
            var previous = onlineWriteSymbol.SelectedItem as OnlineBooleanChoice;
            var choices = OnlineWriteIdentityReady() ? EligibleOnlineBooleans() : [];
            if (!onlineWriteSymbol.Items.Cast<OnlineBooleanChoice>().SequenceEqual(choices))
            {
                onlineWriteSymbol.ItemsSource = choices;
                onlineWriteSymbol.SelectedItem = choices.FirstOrDefault(c => c == previous);
            }
            var selectedChoice = onlineWriteSymbol.SelectedItem as OnlineBooleanChoice;
            var sample = selectedChoice is null ? null : monitorSession?.GetSample(selectedChoice.Symbol.Path,DateTimeOffset.UtcNow);
            onlineWriteCurrent.Text = selectedChoice is null ? "当前值：不可用" : $"{selectedChoice.Display} 当前值：{sample?.Value ?? "不可用"} [{sample?.Quality}]";
            onlineWriteOnce.IsEnabled = !onlineWriteBusy && selectedChoice is not null && onlineWriteValue.SelectedIndex is 0 or 1 && OnlineWriteSelectionReady(selectedChoice);
            onlineWriteSymbol.IsEnabled = onlineWriteValue.IsEnabled = !onlineWriteBusy && choices.Count > 0;
            if (pendingOnlineWrite is { } pending && ReferenceEquals(pending.Session,monitorSession))
            {
                var readback = pending.Session.GetSample(pending.Path, DateTimeOffset.UtcNow);
                if(readback.Quality == SampleQuality.Good && readback.Sequence > pending.Sequence)
                {
                    onlineWriteStatus.Text = $"写入后读回 {pending.Path} = {readback.Value}；" +
                        (readback.Value == (pending.Value ? "TRUE" : "FALSE") ? "与请求值一致（程序仍可覆盖）。" : "与请求值不同，可能已被程序覆盖。并不代表写入未执行。");
                    pendingOnlineWrite = null;
                }
            }
        }
        finally { refreshingOnlineWrite = false; }
    }

    private Task WriteOnlineBooleanAsync() => WriteOnlineBooleanCoreAsync(null);

    private async Task WriteOnlineBooleanCoreAsync(Func<bool>? contextGuard)
    {
        if (onlineWriteBusy || onlineWriteSymbol.SelectedItem is not OnlineBooleanChoice choice ||
            onlineWriteValue.SelectedIndex is not (0 or 1) || !OnlineWriteSelectionReady(choice) || contextGuard?.Invoke() == false) { RefreshOnlineWrite(); return; }
        var value = onlineWriteValue.SelectedIndex == 0;
        var client = debugClient!; var session = monitorSession!; var candidate = candidatePackage;
        var edit = editGeneration; var connection = connectionGeneration; var md5 = activeProgramMd5!;
        onlineWriteBusy = true; RefreshOnlineWrite();
        try
        {
            var message = $"变量：{choice.Display}\n调试路径：{choice.Symbol.Path}\n写入值：{(value ? "TRUE" : "FALSE")}\n程序：{activeBuildHash}\nMD5：{md5}\n仅写入一次，不强制、不自动重试。";
            if(choice.Symbol.Path.EndsWith(".RUNONCE",StringComparison.OrdinalIgnoreCase)) message += "\n警告：RunOnce 可能启用运动，请确认设备现场允许运动。";
            if (!await (onlineWriteConfirmation ?? ConfirmAsync)("确认单次 BOOL 写入",message,"写入一次")) return;
            if (!ReferenceEquals(client,debugClient) || !ReferenceEquals(session,monitorSession) || !ReferenceEquals(candidate,candidatePackage) ||
                edit != editGeneration || connection != connectionGeneration || md5 != activeProgramMd5 ||
                onlineWriteSymbol.SelectedItem as OnlineBooleanChoice != choice || (onlineWriteValue.SelectedIndex == 0) != value || !OnlineWriteSelectionReady(choice) || contextGuard?.Invoke() == false)
            { onlineWriteStatus.Text = "确认期间工程、连接或采样已改变，未发送；请重新核对。"; return; }
            try
            {
                if (onlineBooleanWriter is { } writer) await writer(client,md5,choice.Symbol.Address,value);
                else await client.WriteBooleanAsync(md5,choice.Symbol.Address,value);
                if(!ReferenceEquals(client,debugClient) || !ReferenceEquals(session,monitorSession))
                { onlineWriteStatus.Text = "写入期间连接已改变，结果未知；未重试。"; return; }
                // A poll may have completed during the write; only a later read can confirm its outcome.
                pendingOnlineWrite = (session,choice.Symbol.Path,value,session.GetSample(choice.Symbol.Path,DateTimeOffset.UtcNow).Sequence);
                onlineWriteStatus.Text = "设备已接受单次写入，等待后续采样读回；尚未确认实际值。";
            }
            catch(Exception error)
            {
                pendingOnlineWrite = null;
                if(ReferenceEquals(client,debugClient)) await DisconnectDebugAsync();
                onlineWriteStatus.Text = "写入结果未知，未重试；监视已失效，请重新绑定并核对实际值。" + error.Message;
            }
        }
        finally { onlineWriteBusy = false; RefreshOnlineWrite(); }
    }
}

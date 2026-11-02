using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StWorkbench.Core;
using StWorkbench.OpenPlc;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private sealed record EditorBooleanTarget(OnlineBooleanChoice Choice, int Offset, Guid ItemId, long EditGeneration,
        long ConnectionGeneration, StMonitorContext Context, ReadOnlyMonitorSession Session);
    private readonly MenuItem editorWriteTrue = new() { Header = "写入 TRUE 一次 · 无可用 BOOL", IsEnabled = false };
    private readonly MenuItem editorWriteFalse = new() { Header = "写入 FALSE 一次 · 无可用 BOOL", IsEnabled = false };
    private EditorBooleanTarget? editorBooleanTarget;

    private void InitializeEditorOnlineWrites()
    {
        Register(editorWriteTrue,"EditorWriteTrue"); Register(editorWriteFalse,"EditorWriteFalse");
        editorWriteTrue.Click += async (_, _) => await Run(() => WriteEditorBooleanAsync(true));
        editorWriteFalse.Click += async (_, _) => await Run(() => WriteEditorBooleanAsync(false));
        MenuItem EditingAction(string label, Action action)
        {
            var item = new MenuItem { Header = label }; item.Click += (_, _) => { editor.Focus(); action(); }; return item;
        }
        // Keep the ordinary editor actions beside the explicit online-only operations.
        editor.ContextMenu = new ContextMenu { ItemsSource = new object[]
        {
            EditingAction("撤销", () => editor.Undo()), EditingAction("重做", () => editor.Redo()), new Separator(),
            EditingAction("剪切", () => editor.Cut()), EditingAction("复制", () => editor.Copy()),
            EditingAction("粘贴", () => editor.Paste()), EditingAction("全选", editor.SelectAll), new Separator(), editorWriteTrue, editorWriteFalse
        } };
        editor.AddHandler(ContextRequestedEvent, (_, args) =>
        {
            // Pointer requests resolve their own hit position. Only keyboard requests use the caret.
            var offset = editor.CaretOffset;
            if(args.TryGetPosition(editor,out var point))
            {
                var position = editor.GetPositionFromPoint(point);
                offset = position is { } hit ? editor.Document.GetOffset(hit.Line,hit.Column) : -1;
            }
            PrepareEditorOnlineWrite(offset);
        }, RoutingStrategies.Tunnel, true);
    }

    private OnlineBooleanChoice? ResolveEditorOnlineBoolean(int offset)
    {
        if(offset < 0 || offset >= editor.Document.TextLength || selected is null ||
            !sources.TryGetValue(selected.Id,out var doc) || doc.Text != editor.Text ||
            monitorInstance.SelectedItem is not StMonitorContext context || !OnlineWriteIdentityReady()) return null;
        var choices = EligibleOnlineBooleans();
        var references = language.MonitorReferences(doc,context,debugSymbols.Select(s => s.Path).ToArray());
        foreach(var reference in references)
        {
            var choice = choices.SingleOrDefault(c => c.Symbol.Path.Equals(reference.Path,StringComparison.OrdinalIgnoreCase));
            if(choice is null) continue;
            // MonitorReferences already excludes strings, comments, other POUs and argument labels.
            // Its end offset plus the exact mapped leaf bounds the identifier; whitespace is never guessed.
            var name = choice.Symbol.Path.Split('.').Last();
            var start = reference.EndOffset - name.Length;
            if(offset >= start && offset < reference.EndOffset &&
                editor.Document.GetText(start,name.Length).Equals(name,StringComparison.OrdinalIgnoreCase) && OnlineWriteSelectionReady(choice)) return choice;
        }
        return null;
    }

    private void PrepareEditorOnlineWrite(int offset)
    {
        var choice = ResolveEditorOnlineBoolean(offset);
        editorBooleanTarget = choice is null ? null : new(choice,offset,selected!.Id,editGeneration,connectionGeneration,
            (StMonitorContext)monitorInstance.SelectedItem!,monitorSession!);
        editorWriteTrue.IsEnabled = editorWriteFalse.IsEnabled = choice is not null && !onlineWriteBusy;
        var detail = choice is null ? "无可用 BOOL（离线、过期或未命中用户变量）" :
            $"{choice.Display}（当前 {monitorSession!.GetSample(choice.Symbol.Path,DateTimeOffset.UtcNow).Value}）";
        editorWriteTrue.Header = "写入 TRUE 一次 · " + detail;
        editorWriteFalse.Header = "写入 FALSE 一次 · " + detail;
    }

    private async Task WriteEditorBooleanAsync(bool value)
    {
        var target = editorBooleanTarget;
        if(target is null || onlineWriteBusy || !EditorBooleanTargetMatches(target)) return;
        RefreshOnlineWrite();
        onlineWriteSymbol.SelectedItem = onlineWriteSymbol.Items.OfType<OnlineBooleanChoice>().SingleOrDefault(c => c == target.Choice);
        onlineWriteValue.SelectedIndex = value ? 0 : 1;
        // One path owns identity checks, confirmation, protocol dispatch and unknown outcomes.
        await WriteOnlineBooleanCoreAsync(() => EditorBooleanTargetMatches(target));
    }

    private bool EditorBooleanTargetMatches(EditorBooleanTarget target) => selected?.Id == target.ItemId && editGeneration == target.EditGeneration &&
        connectionGeneration == target.ConnectionGeneration && ReferenceEquals(monitorSession,target.Session) &&
        monitorInstance.SelectedItem as StMonitorContext == target.Context && ResolveEditorOnlineBoolean(target.Offset) == target.Choice;
}

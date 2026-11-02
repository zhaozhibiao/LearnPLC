using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StWorkbench.App;

internal static class ConfirmationDialogUiTests
{
    public static void Run()
    {
        var owner = new MainWindow(); owner.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            foreach (var accept in new[] { false, true })
            {
                var text = string.Join('\n', Enumerable.Range(0, 36).Select(i => $"变量与程序身份 {i}: " + new string('A',64)));
                var operation = (Task<bool>)typeof(MainWindow).GetMethod("ConfirmAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner,["确认单次 BOOL 写入",text,"写入一次"])!;
                Dispatcher.UIThread.RunJobs();
                var dialog = owner.OwnedWindows.Single();
                if (!dialog.CanResize || !dialog.GetVisualDescendants().OfType<ScrollViewer>().Any()) throw new Exception("FAIL confirmation message cannot scroll/resize");
                dialog.Height = 240; dialog.Width = 420; Dispatcher.UIThread.RunJobs();
                var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content,"写入一次") || Equals(b.Content,"取消")).ToArray();
                if(buttons.Length!=2) throw new Exception("FAIL confirmation actions missing");
                foreach(var button in buttons)
                {
                    var point = button.TranslatePoint(default,dialog) ?? throw new Exception("FAIL action detached");
                    if(button.Bounds.Height<28 || point.Y<0 || point.Y+button.Bounds.Height>dialog.Bounds.Height) throw new Exception("FAIL confirmation action clipped below viewport");
                }
                var action = buttons.Single(b => Equals(b.Content,accept ? "写入一次" : "取消"));
                var center = action.TranslatePoint(new Point(action.Bounds.Width/2,action.Bounds.Height/2),dialog)!.Value;
                dialog.MouseDown(center, MouseButton.Left); dialog.MouseUp(center, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                if(!operation.IsCompleted || operation.GetAwaiter().GetResult()!=accept) throw new Exception("FAIL visible confirmation button cannot be clicked");
            }
            Console.WriteLine("PASS long confirmation text scrolls and both actions remain mouse-clickable at reduced window height");
        }
        finally { foreach(var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); typeof(MainWindow).GetField("allowClose",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,true); owner.Close(); }
    }
}

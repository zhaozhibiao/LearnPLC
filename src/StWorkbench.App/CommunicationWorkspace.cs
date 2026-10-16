using Avalonia.Controls;
using Avalonia.Layout;
using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private readonly ListBox communicationChannels = new() { Height = 145 };
    private readonly TextBox communicationName = new() { Text = "Channel1", Width = 160 };
    private readonly ComboBox communicationProtocol = new() { ItemsSource = new[] { "ModbusTcp", "ModbusRtu", "Serial", "Tcp" }, SelectedIndex = 0, Width = 150 };
    private readonly Dictionary<string, TextBox> communicationFields = new();
    private readonly Dictionary<string, ComboBox> communicationChoices = new();
    private readonly TextBlock communicationMessage = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock networkCommunicationSummary = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private Control CommunicationPane()
    {
        Register(communicationChannels, "CommunicationChannels"); Register(communicationName, "CommunicationName"); Register(communicationProtocol, "CommunicationProtocol");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Label("PLC 端通信通道 · 主站 / 客户端。配置保存到工程，实际连接和收发由 ST 功能块发起。"));
        panel.Children.Add(communicationChannels);
        panel.Children.Add(new WrapPanel { Children = { Label("通道名称"), communicationName, Label("协议"), communicationProtocol } });
        var defaults = new CommunicationChannel();
        var rows = new WrapPanel();
        foreach (var (key, label, value) in new[] { ("Host", "目标 IP", defaults.Host), ("Port", "目标端口", "502"), ("Device", "Linux 串口", defaults.Device), ("BaudRate", "波特率", "9600"), ("TimeoutMilliseconds", "超时 ms", "1000"), ("BeforeSendMicroseconds", "发送前延时 µs", "0"), ("AfterSendMicroseconds", "发送后延时 µs", "0"), ("FixedLength", "固定帧长", "1"), ("DelimiterHex", "结束符 HEX", "0D0A"), ("IdleMilliseconds", "空闲组帧 ms", "20") })
        {
            var editor = new TextBox { Text = value, Width = key == "Device" ? 220 : 130 }; communicationFields[key] = editor;
            Register(editor, "Communication" + key); rows.Children.Add(new StackPanel { Margin = new Avalonia.Thickness(0, 0, 10, 8), Children = { Label(label), editor } });
        }
        panel.Children.Add(rows);
        var choices = new WrapPanel();
        foreach (var (key, label, values) in new[] { ("DataBits", "数据位", new[] { "8", "7" }), ("Parity", "校验", new[] { "Even", "Odd", "None" }), ("StopBits", "停止位", new[] { "1", "2" }), ("Physical", "串口类型", new[] { "RS232", "RS485" }), ("Direction", "485 方向", new[] { "Automatic", "KernelRts" }), ("Framing", "自由协议组帧", new[] { "Raw", "Fixed", "Delimiter", "Length16", "Idle" }) })
        {
            var editor = new ComboBox { ItemsSource = values, SelectedIndex = 0, Width = 130 }; communicationChoices[key] = editor;
            Register(editor, "Communication" + key); choices.Children.Add(new StackPanel { Margin = new Avalonia.Thickness(0, 0, 10, 8), Children = { Label(label), editor } });
        }
        panel.Children.Add(choices);
        var buttons = new WrapPanel();
        AddConfigurationButton(buttons, "添加通道", "AddCommunicationChannel", () => ApplyCommunicationChannel(false));
        AddConfigurationButton(buttons, "应用选中通道", "ApplyCommunicationChannel", () => ApplyCommunicationChannel(true));
        AddConfigurationButton(buttons, "删除通道", "DeleteCommunicationChannel", DeleteCommunicationChannel);
        panel.Children.Add(buttons); panel.Children.Add(communicationMessage);
        panel.Children.Add(Label("RS485 Automatic 表示转换器自行控制方向；KernelRts 需要 Linux 驱动支持。字节数据最大 512；寄存器地址从 0 开始，跨寄存器字序由 ST 显式转换。"));
        communicationChannels.SelectionChanged += (_, _) =>
        {
            var channels = project.Communication?.Channels ?? [];
            if (communicationChannels.SelectedIndex < 0 || communicationChannels.SelectedIndex >= channels.Length) return;
            var c = channels[communicationChannels.SelectedIndex]; communicationName.Text = c.Name; communicationProtocol.SelectedItem = c.Protocol;
            foreach (var pair in communicationFields) pair.Value.Text = typeof(CommunicationChannel).GetProperty(pair.Key)!.GetValue(c)?.ToString();
            foreach (var pair in communicationChoices) pair.Value.SelectedItem = typeof(CommunicationChannel).GetProperty(pair.Key)!.GetValue(c)?.ToString();
        };
        return panel;
    }
    private void RefreshCommunication()
    {
        var channels = project.Communication?.Channels ?? []; var index = communicationChannels.SelectedIndex;
        communicationChannels.ItemsSource = channels.Select(c => $"{c.Name} · {c.Protocol} · {(c.Protocol is "Tcp" or "ModbusTcp" ? c.Host + ":" + c.Port : c.Device + " / " + c.BaudRate)}").ToArray();
        communicationChannels.SelectedIndex = index >= 0 && index < channels.Length ? index : -1;
        networkCommunicationSummary.Text = channels.Length == 0 ? "尚无通信通道。" : string.Join("\n", channels.Select(c => $"{c.Name} → {(c.Protocol is "Tcp" or "ModbusTcp" ? c.Host + ":" + c.Port : c.Device)} · {c.Protocol} · 离线配置，实际连接由程序执行。"));
    }
    private void ApplyCommunicationChannel(bool replace)
    {
        int Number(string key) => int.TryParse(communicationFields[key].Text, out var value) ? value : throw new InvalidDataException("通信参数必须是整数：" + key);
        string Text(string key) => communicationFields[key].Text?.Trim() ?? "";
        string Choice(string key) => communicationChoices[key].SelectedItem?.ToString() ?? "";
        var next = new CommunicationChannel { Name = communicationName.Text?.Trim() ?? "", Protocol = communicationProtocol.SelectedItem?.ToString() ?? "", Host = Text("Host"), Port = Number("Port"), Device = Text("Device"), BaudRate = Number("BaudRate"), TimeoutMilliseconds = Number("TimeoutMilliseconds"), BeforeSendMicroseconds = Number("BeforeSendMicroseconds"), AfterSendMicroseconds = Number("AfterSendMicroseconds"), FixedLength = Number("FixedLength"), DelimiterHex = Text("DelimiterHex"), IdleMilliseconds = Number("IdleMilliseconds"), DataBits = int.Parse(Choice("DataBits")), Parity = Choice("Parity"), StopBits = int.Parse(Choice("StopBits")), Physical = Choice("Physical"), Direction = Choice("Direction"), Framing = Choice("Framing") };
        var previous = project.Communication; var channels = (previous?.Channels ?? []).ToList(); var at = communicationChannels.SelectedIndex;
        if (replace) { if (at < 0 || at >= channels.Count) throw new InvalidOperationException("请先选择通道。"); channels[at] = next; } else channels.Add(next);
        ChangeHardware(() => project.Communication = new() { Channels = channels.ToArray() }, () => project.Communication = previous);
        communicationMessage.Text = "通信通道已更新到工程草稿；请保存、编译并部署后查询实际执行结果。";
    }
    private void DeleteCommunicationChannel()
    {
        var previous = project.Communication; var channels = (previous?.Channels ?? []).ToList(); var at = communicationChannels.SelectedIndex;
        if (at < 0 || at >= channels.Count) throw new InvalidOperationException("请先选择通道。");
        var identifier = "COMM_" + channels[at].Name;
        if (GetSources().Any(s => new StLanguageService().RenameLocalIdentifier(s.Text, identifier, "McpCommunicationReferenceProbe") != s.Text)) throw new InvalidOperationException("程序仍引用该通信通道，请先移除引用。");
        channels.RemoveAt(at); ChangeHardware(() => project.Communication = channels.Count == 0 ? null : new() { Channels = channels.ToArray() }, () => project.Communication = previous);
    }
}

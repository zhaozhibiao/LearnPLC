using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private readonly TextBox networkManagementHost = new() { Width = 260, PlaceholderText = "PLC 管理地址，例如 192.168.1.31" };
    private readonly TextBox networkSshUser = new() { Width = 160, PlaceholderText = "Linux 用户名" };
    private readonly TextBox networkRuntimeEndpoint = new() { Width = 360, PlaceholderText = "可选：直接连接的 https://主机:8443" };
    private readonly TextBox etherCatInterface = new() { Width = 220, HorizontalAlignment=HorizontalAlignment.Left, PlaceholderText = "Linux 网卡，例如 enp2s0" };
    private readonly ListBox networkStations = new() { Height = 170 };
    private readonly TreeView networkTopology = new() { MinHeight = 160 };
    private readonly TextBlock networkConfigurationStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#7ECED4") };
    private readonly TextBlock networkActualConnection = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A9BAD1") };
    private Control NetworkConfigurationPane()
    {
        foreach (var pair in new (Control Control,string Name)[] { (networkManagementHost,"NetworkManagementHost"),(networkSshUser,"NetworkSshUser"),(networkRuntimeEndpoint,"NetworkRuntimeEndpoint"),(etherCatInterface,"EtherCatInterface"),(networkStations,"NetworkStations"),(networkTopology,"NetworkTopology"),(networkConfigurationStatus,"NetworkConfigurationStatus"),(networkActualConnection,"NetworkActualConnection") }) Register(pair.Control,pair.Name);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "网络组态", FontSize = 22, FontWeight = FontWeight.Bold });
        panel.Children.Add(Label("管理网络用于连接 PLC；EtherCAT 网卡和从站链用于总线组态。普通 EtherCAT 从站不配置 IP。"));
        panel.Children.Add(Label("PLC 管理连接"));
        StackPanel Field(string name, Control editor) => new() { Spacing=4, Children={ new TextBlock{Text=name,Foreground=Brush.Parse("#A9BAD1")}, editor } };
        panel.Children.Add(new WrapPanel { Children = { Field("PLC 地址",networkManagementHost), Field("Linux 用户名",networkSshUser), Field("直接 HTTPS（可选）",networkRuntimeEndpoint) } });
        panel.Children.Add(Label("EtherCAT 主站接口（设备端 Linux 网卡名称）")); panel.Children.Add(etherCatInterface);
        var buttons = new WrapPanel();
        AddConfigurationButton(buttons,"应用网络组态到工程","ApplyNetworkConfiguration",ApplyNetworkConfiguration);
        AddConfigurationButton(buttons,"从当前连接设置读取","ReadNetworkConnection",()=> { networkManagementHost.Text=sshHost.Text; networkSshUser.Text=sshUser.Text; networkRuntimeEndpoint.Text=endpoint.Text; });
        AddConfigurationButton(buttons,"清除工程网络配置","ClearNetworkConfiguration",ClearNetworkConfiguration); panel.Children.Add(buttons);
        panel.Children.Add(networkConfigurationStatus);
        panel.Children.Add(Label("应用通信端点 · 与硬件组态共享")); panel.Children.Add(networkCommunicationSummary);
        panel.Children.Add(ActionButton("配置通信通道", () => { NavigateTo(2); return Task.CompletedTask; }));
        panel.Children.Add(Label("工程拓扑（离线设定，未核验实物）")); panel.Children.Add(networkTopology);
        panel.Children.Add(Label("从站连接顺序 · 与硬件组态共享")); panel.Children.Add(networkStations);
        var stationButtons = new WrapPanel();
        AddConfigurationButton(stationButtons,"上移从站","NetworkStationUp",()=>MoveConfiguredStation(-1));
        AddConfigurationButton(stationButtons,"下移从站","NetworkStationDown",()=>MoveConfiguredStation(1));
        AddConfigurationButton(stationButtons,"打开从站硬件属性","OpenStationHardware",()=>NavigateTo(2)); panel.Children.Add(stationButtons);
        networkStations.SelectionChanged += (_,_)=> { if(!refreshingConfiguration)SelectConfiguredStation(networkStations.SelectedIndex); };
        panel.Children.Add(Label("实际管理连接")); panel.Children.Add(networkActualConnection);
        var connect = ActionButton("连接工程服务（免 SSH）",ConnectConfiguredRuntimeAsync); Register(connect,"NetworkConnectRuntime");panel.Children.Add(connect);
        panel.Children.Add(Label("应用按钮只保存工程配置并更新本次连接目标；不会改写设备 IP、路由或网卡，不会启动运动服务或下载总线参数。密码和密钥不保存在工程内。"));
        var scroll=ScrollPanel(panel);Register(scroll,"NetworkConfigurationScroll");return scroll;
    }
    public void ApplyNetworkConfiguration()
    {
        if (connecting) throw new InvalidOperationException("连接尚未结束，请等待后应用网络组态。");
        var next=new NetworkConfiguration(networkManagementHost.Text?.Trim()??"",networkSshUser.Text?.Trim()??"",networkRuntimeEndpoint.Text?.Trim()??"",etherCatInterface.Text?.Trim()??"");
        var previous=project.Network;
        ChangeHardware(()=>project.Network=next,()=>project.Network=previous);
        UseProjectNetworkTarget(); RefreshNetworkTopology();
        networkConfigurationStatus.Text="网络组态已保存到工程草稿；本次连接目标已更新。设备 IP / 总线设置尚未应用。";
    }
    public void ClearNetworkConfiguration()
    {
        if(connecting)throw new InvalidOperationException("连接尚未结束。");
        var previous=project.Network; ChangeHardware(()=>project.Network=null,()=>project.Network=previous); RestoreNetworkConfiguration();
        networkConfigurationStatus.Text="已清除工程网络配置，连接与工具页的现有目标保持不变。";
    }
    private void UseProjectNetworkTarget()
    {
        if (project.Network is not { } network) return;
        var endpointChanged=endpoint.Text!=network.RuntimeEndpoint;
        var changed=sshHost.Text!=network.ManagementHost||sshUser.Text!=network.SshUser||endpointChanged;
        if(changed)
        {
            ResetDeviceIdentity(); password.Text="";
            sshHost.Text=network.ManagementHost;sshUser.Text=network.SshUser;
            endpoint.Text=network.RuntimeEndpoint;
        }
    }
    public async Task ConnectConfiguredRuntimeAsync()
    {
        var expected=project.Network??new NetworkConfiguration(sshHost.Text??"",sshUser.Text??"",endpoint.Text??"","");
        var draft=new NetworkConfiguration(networkManagementHost.Text?.Trim()??"",networkSshUser.Text?.Trim()??"",networkRuntimeEndpoint.Text?.Trim()??"",etherCatInterface.Text?.Trim()??"");
        if(draft!=expected)throw new InvalidOperationException("网络组态有尚未应用的修改，请先点击“应用网络组态到工程”。");
        UseProjectNetworkTarget();
        try { await ConnectEngineeringAsync(); }
        finally { RefreshNetworkTopology(); }
    }
    private void RestoreNetworkConfiguration()
    {
        var network=project.Network;
        networkManagementHost.Text=network?.ManagementHost??sshHost.Text;
        networkSshUser.Text=network?.SshUser??sshUser.Text;
        networkRuntimeEndpoint.Text=network?.RuntimeEndpoint??endpoint.Text;
        etherCatInterface.Text=network?.EtherCatInterface??"";
        networkConfigurationStatus.Text=network is null?"尚未配置工程网络；可以读取当前连接设置后应用。":"已加载工程网络配置；设备 IP / 总线设置尚未应用。";
        UseProjectNetworkTarget(); RefreshNetworkTopology();
    }
    private void RefreshNetworkTopology()
    {
        var network=project.Network;
        var management=new TreeViewItem { Header=$"工程站 → PLC 管理连接：{network?.ManagementHost??"未配置"}",IsExpanded=true,ItemsSource=new[]{new TreeViewItem{Header=$"SSH 用户：{network?.SshUser??"未配置"}"},new TreeViewItem{Header=$"直接 HTTPS：{(string.IsNullOrWhiteSpace(network?.RuntimeEndpoint)?"未配置 / 使用 SSH 隧道":network.RuntimeEndpoint)}"}} };
        var children=Stations.Select((s,i)=>new TreeViewItem { Header=$"{i+1} → {s.Name} · {project.Hardware.Devices.FirstOrDefault(d=>d.Id==s.DeviceId)?.Name??"未知设备"} · 别名 {s.AliasAddress}",ContextMenu=NavigationMenu("硬件组态 / 从站属性",2)}).ToArray();
        var master=new TreeViewItem{Header=$"PLC → EtherCAT 主站：{(string.IsNullOrWhiteSpace(network?.EtherCatInterface)?"网卡未配置":network.EtherCatInterface)}",IsExpanded=true,ItemsSource=children};
        networkTopology.ItemsSource=new[]{management,master};
        networkActualConnection.Text=$"当前连接目标：{sshHost.Text} / {sshUser.Text}\n{(runtime?.IsAuthenticated==true?"Runtime 已认证；实际运行状态见顶部设备状态。":"Runtime 未认证或已断开。")}\nEtherCAT 实物状态：未核验；工程拓扑不代表设备已连接。";
    }
}

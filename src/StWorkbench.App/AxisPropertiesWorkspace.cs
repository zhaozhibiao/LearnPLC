using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private readonly CheckBox configureAxisMechanics = new() { Content = "配置机械、反馈、旋转与回零属性（离线工程配置）" };
    private readonly ComboBox axisKind = new() { ItemsSource = new[] { "直线轴", "旋转轴" }, SelectedIndex = 0, Width = 220 };
    private readonly ComboBox axisFeedbackUnit = new() { ItemsSource = new[] { "驱动器已换算为工程单位", "原始反馈计数" }, SelectedIndex = 0, Width = 260 };
    private readonly ComboBox axisRotaryMode = new() { ItemsSource = new[] { "有限角度", "无限旋转 · 累计角度", "周期角度 · Modulo" }, SelectedIndex = 0, Width = 260 };
    private readonly ComboBox axisHomeMode = new() { ItemsSource = new[] { "沿用控制器回零配置", "原点开关", "编码器索引", "原点开关 + 索引" }, SelectedIndex = 0, Width = 260 };
    private readonly ComboBox axisHomeDirection = new() { ItemsSource = new[] { "正向", "负向" }, SelectedIndex = 1, Width = 150 };
    private readonly CheckBox axisReverseDirection = new() { Content = "反转工程轴正方向" };
    private readonly Dictionary<string, TextBox> axisMechanicalNumbers = new();
    private readonly StackPanel axisMechanicalForm = new() { Spacing = 7 }, axisLinearForm = new() { Spacing = 5 }, axisRotaryForm = new() { Spacing = 5 }, axisModuloForm = new() { Spacing = 5 }, axisHomeForm = new() { Spacing = 5 };
    private readonly TextBlock axisConversionPreview = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#7ECED4") };
    private bool loadingAxisMechanics;

    private Control AxisMechanicalPropertiesPane()
    {
        Register(configureAxisMechanics, "ConfigureAxisMechanics"); Register(axisKind, "AxisKind");
        Register(axisFeedbackUnit, "AxisFeedbackUnit"); Register(axisRotaryMode, "AxisRotaryMode");
        Register(axisHomeMode, "AxisHomeMode"); Register(axisHomeDirection, "AxisHomeDirection");
        Register(axisReverseDirection, "AxisReverseDirection"); Register(axisConversionPreview, "AxisConversionPreview");
        Control Row(string title, Control value) => new WrapPanel { Children = { new TextBlock { Text = title, Width = 245, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, value } };
        void Number(StackPanel target, string key, string title, string initial)
        {
            var field = new TextBox { Text = initial, Width = 180, Tag = title };
            axisMechanicalNumbers.Add(key, field); Register(field, "Axis" + key);
            field.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RefreshAxisMechanicalPreview(); };
            target.Children.Add(Row(title, field));
        }
        axisMechanicalForm.Children.Add(Row("轴类型", axisKind));
        axisMechanicalForm.Children.Add(Row("控制器位置/速度的单位", axisFeedbackUnit));
        Number(axisMechanicalForm, "FeedbackCounts", "每电机转反馈计数（非编码器线数）", "131072");
        Number(axisMechanicalForm, "MotorRevolutions", "减速比 · 电机转数", "1");
        Number(axisMechanicalForm, "LoadRevolutions", "减速比 · 负载转数", "1");
        axisMechanicalForm.Children.Add(Label("例如 10:1：电机转 10 圈，负载转 1 圈。反馈计数应取驱动器实际输出分辨率。"));
        Number(axisLinearForm, "TravelPerRevolution", "每负载转移动距离 / mm", "5");
        axisMechanicalForm.Children.Add(axisLinearForm);
        axisRotaryForm.Children.Add(Row("旋转方式", axisRotaryMode));
        Number(axisModuloForm, "ModuloStart", "周期起点 / 工程单位", "0");
        Number(axisModuloForm, "ModuloPeriod", "周期长度 / 工程单位", "360");
        axisRotaryForm.Children.Add(axisModuloForm);
        axisRotaryForm.Children.Add(Label("旋转单位填 deg（度）或 rev（圈）。周期定位方向属于单次运动参数，当前执行接口尚未支持。"));
        axisMechanicalForm.Children.Add(axisRotaryForm);
        axisMechanicalForm.Children.Add(axisReverseDirection);
        axisMechanicalForm.Children.Add(Row("回零方式", axisHomeMode));
        axisHomeForm.Children.Add(Row("回零搜索方向", axisHomeDirection));
        Number(axisHomeForm, "HomeOffset", "回零偏移 / 工程单位", "0");
        Number(axisHomeForm, "HomeSearchVelocity", "回零搜索速度 / 工程单位每秒", "1");
        Number(axisHomeForm, "HomeLatchVelocity", "回零慢速 / 工程单位每秒", "0.2");
        axisMechanicalForm.Children.Add(axisHomeForm);
        axisMechanicalForm.Children.Add(axisConversionPreview);
        axisMechanicalForm.Children.Add(Label("机械与回零属性尚未应用到设备。当前运动核心缺少参数应用/确认接口；带这些新属性的候选可编译，但不能激活执行。"));
        foreach (var combo in new[] { axisKind, axisFeedbackUnit, axisRotaryMode, axisHomeMode, axisHomeDirection })
            combo.SelectionChanged += (_, _) =>
            {
                if (!loadingAxisMechanics && ReferenceEquals(combo, axisKind))
                {
                    axisUnitEditor.Text = axisKind.SelectedIndex == 1 ? "deg" : "mm";
                    if (axisKind.SelectedIndex == 0) axisRotaryMode.SelectedIndex = (int)RotaryMode.Limited;
                    axisMinEditor.Text = axisKind.SelectedIndex == 1 ? "0" : "-1000";
                    axisMaxEditor.Text = axisKind.SelectedIndex == 1 ? "360" : "1000";
                    axisMechanicalNumbers["ModuloStart"].Text = "0"; axisMechanicalNumbers["ModuloPeriod"].Text = "360";
                }
                RefreshAxisMechanicalPreview();
            };
        configureAxisMechanics.IsCheckedChanged += (_, _) => RefreshAxisMechanicalPreview();
        axisUnitEditor.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RefreshAxisMechanicalPreview(); };
        var body = new StackPanel { Spacing = 8, Children = { configureAxisMechanics, axisMechanicalForm } };
        RefreshAxisMechanicalPreview();
        return new Expander { Header = "轴属性 · 机械 / 反馈 / 旋转 / 回零", IsExpanded = true, Content = body };
    }

    private AxisMechanics? ReadAxisMechanics()
    {
        if (configureAxisMechanics.IsChecked != true) return null;
        double Number(string key)
        {
            if (!double.TryParse(axisMechanicalNumbers[key].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidDataException("轴属性需要有效的有限数值：" + axisMechanicalNumbers[key].Tag);
            return value;
        }
        return new AxisMechanics
        {
            Kind = (AxisKind)axisKind.SelectedIndex, FeedbackUnit = (AxisFeedbackUnit)axisFeedbackUnit.SelectedIndex,
            FeedbackCountsPerMotorRevolution = Number("FeedbackCounts"), MotorRevolutions = Number("MotorRevolutions"), LoadRevolutions = Number("LoadRevolutions"),
            TravelPerLoadRevolution = Number("TravelPerRevolution"), ReverseDirection = axisReverseDirection.IsChecked == true,
            RotaryMode = (RotaryMode)axisRotaryMode.SelectedIndex, ModuloStart = Number("ModuloStart"), ModuloPeriod = Number("ModuloPeriod"),
            HomeMode = (AxisHomeMode)axisHomeMode.SelectedIndex, HomeDirection = (AxisHomeDirection)axisHomeDirection.SelectedIndex,
            HomeOffset = Number("HomeOffset"), HomeSearchVelocity = Number("HomeSearchVelocity"), HomeLatchVelocity = Number("HomeLatchVelocity")
        };
    }

    private void LoadAxisMechanics(AxisConfiguration axis)
    {
        loadingAxisMechanics = true;
        try
        {
            var value = axis.Mechanics ?? new AxisMechanics();
            configureAxisMechanics.IsChecked = axis.Mechanics is not null;
            axisKind.SelectedIndex = (int)value.Kind; axisFeedbackUnit.SelectedIndex = (int)value.FeedbackUnit;
            axisRotaryMode.SelectedIndex = (int)value.RotaryMode; axisHomeMode.SelectedIndex = (int)value.HomeMode; axisHomeDirection.SelectedIndex = (int)value.HomeDirection;
            axisReverseDirection.IsChecked = value.ReverseDirection;
            void Set(string key, double number) => axisMechanicalNumbers[key].Text = number.ToString("G", CultureInfo.InvariantCulture);
            Set("FeedbackCounts", value.FeedbackCountsPerMotorRevolution); Set("MotorRevolutions", value.MotorRevolutions); Set("LoadRevolutions", value.LoadRevolutions);
            Set("TravelPerRevolution", value.TravelPerLoadRevolution); Set("ModuloStart", value.ModuloStart); Set("ModuloPeriod", value.ModuloPeriod);
            Set("HomeOffset", value.HomeOffset); Set("HomeSearchVelocity", value.HomeSearchVelocity); Set("HomeLatchVelocity", value.HomeLatchVelocity);
        }
        finally { loadingAxisMechanics = false; }
        RefreshAxisMechanicalPreview();
    }

    private void RefreshAxisMechanicalPreview()
    {
        if (loadingAxisMechanics || axisMechanicalNumbers.Count < 9) return;
        var enabled = configureAxisMechanics.IsChecked == true;
        axisMechanicalForm.IsEnabled = enabled;
        axisLinearForm.IsVisible = axisKind.SelectedIndex == 0;
        axisRotaryForm.IsVisible = axisKind.SelectedIndex == 1;
        axisModuloForm.IsVisible = axisRotaryMode.SelectedIndex == 2;
        axisHomeForm.IsVisible = axisHomeMode.SelectedIndex != 0;
        if (!enabled) { axisConversionPreview.Text = "沿用现有控制器轴配置；未添加机械参数。"; return; }
        try
        {
            var mechanics = ReadAxisMechanics()!;
            var axis = new AxisConfiguration("Preview", "Preview", axisUnitEditor.Text?.Trim() ?? "", -1e12, 1e12) { Mechanics = mechanics };
            var errors = AxisMechanicsValidator.Validate(axis);
            if (errors.Count > 0) throw new InvalidDataException(string.Join("；", errors));
            var counts = AxisMechanicsConversion.CountsPerUnit(axis);
            var scaling = mechanics.FeedbackUnit == AxisFeedbackUnit.EngineeringUnits ? "驱动器已换算，工作进程不重复换算；以下仅为机械比例参考。" : "原始反馈计数需要控制核心按此比例换算（当前尚未应用）。";
            axisConversionPreview.Text = scaling + $"\n每 {axis.Unit} = {counts.ToString("0.#########", CultureInfo.InvariantCulture)} 个反馈计数。";
        }
        catch (InvalidDataException error) { axisConversionPreview.Text = "待修正：" + error.Message; }
        catch (ArgumentException error) { axisConversionPreview.Text = "待修正：" + error.Message; }
    }
}

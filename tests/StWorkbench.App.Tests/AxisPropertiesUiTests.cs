using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using StWorkbench.App;
using StWorkbench.Core;
using StWorkbench.OpenPlc;
using StWorkbench.Toolchain;

public static class AxisPropertiesUiTests
{
    public static void Run()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            T Control<T>(string name) where T : Control => window.FindControl<T>(name) ?? throw new Exception("FAIL: missing axis property control " + name);
            Control<CheckBox>("ConfigureAxisMechanics").IsChecked = true;
            Control<ComboBox>("AxisKind").SelectedIndex = 1;
            if (Control<TextBox>("AxisUnit").Text != "deg") throw new Exception("FAIL: rotary axis must default to angle units");
            Control<TextBox>("AxisName").Text = "Turntable";
            Control<TextBox>("ControllerAxis").Text = "Rotary1";
            Control<TextBox>("AxisMotorRevolutions").Text = "10";
            Control<TextBox>("AxisFeedbackCounts").Text = "131072";
            Control<ComboBox>("AxisFeedbackUnit").SelectedIndex = 1;
            window.SaveAxisProperties(false); Dispatcher.UIThread.RunJobs();
            var project = (StProject)typeof(MainWindow).GetField("project", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            using var axis = JsonDocument.Parse(JsonSerializer.Serialize(project.Hardware.Axes.Single()));
            var mechanics = axis.RootElement.GetProperty("Mechanics");
            if (mechanics.GetProperty("Kind").GetString() != "Rotary" || mechanics.GetProperty("MotorRevolutions").GetDouble() != 10) throw new Exception("FAIL: mechanical configuration not saved");
            Control<ListBox>("AxisConfigurations").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            if (Control<TextBox>("AxisMotorRevolutions").Text != "10") throw new Exception("FAIL: axis selection lost ratio");
            if (!Control<TextBlock>("AxisConversionPreview").Text!.Contains("3640.888")) throw new Exception("FAIL: counts/angle preview incorrect");
            var old = project.Hardware.Axes[0];
            Control<TextBox>("AxisMotorRevolutions").Text = "0";
            try { window.SaveAxisProperties(true); throw new Exception("FAIL: invalid ratio accepted"); } catch (InvalidDataException) { }
            if (project.Hardware.Axes[0] != old) throw new Exception("FAIL: rejected axis edit mutated engineering configuration");
            Control<TextBox>("AxisMotorRevolutions").Text = "10";
            Control<ComboBox>("AxisFeedbackUnit").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            if (!Control<TextBlock>("AxisConversionPreview").Text!.Contains("不重复换算")) throw new Exception("FAIL: engineering feedback must explain scaling ownership");
            window.SaveAxisProperties(true);
            var file = Path.GetFullPath("artifacts/axis-properties-ui/Rotary.stproj");
            Pump(window.SaveAtAsync(file)); Pump(window.LoadAtAsync(file));
            Control<ListBox>("AxisConfigurations").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            if (Control<TextBox>("AxisMotorRevolutions").Text != "10" || Control<ComboBox>("AxisKind").SelectedIndex != 1) throw new Exception("FAIL: rotary configuration did not survive save/reopen");
            Control<ComboBox>("AxisRotaryMode").SelectedIndex = 2;
            Control<TextBox>("AxisModuloPeriod").Text = "0";
            try { window.SaveAxisProperties(true); throw new Exception("FAIL: zero rotary period accepted"); } catch (InvalidDataException) { }
            Control<TextBox>("AxisModuloPeriod").Text = "360";
            Control<ComboBox>("AxisHomeMode").SelectedIndex = 1;
            Control<TextBox>("AxisHomeSearchVelocity").Text = "2";
            Control<TextBox>("AxisHomeLatchVelocity").Text = "3";
            try { window.SaveAxisProperties(true); throw new Exception("FAIL: latch faster than search accepted"); } catch (InvalidDataException) { }
            Control<TextBox>("AxisHomeLatchVelocity").Text = "0.2";
            window.SaveAxisProperties(true);
            Control<ListBox>("AxisConfigurations").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            if (Control<ComboBox>("AxisHomeMode").SelectedIndex != 1 || Control<TextBox>("AxisHomeSearchVelocity").Text != "2") throw new Exception("FAIL: homing configuration lost on selection");
            Control<ComboBox>("AxisKind").SelectedIndex = 0;
            Control<TextBox>("AxisName").Text = "Slide"; Control<TextBox>("ControllerAxis").Text = "Slide1";
            Control<TextBox>("AxisTravelPerRevolution").Text = "5";
            window.SaveAxisProperties(false);
            if (!Control<TextBlock>("AxisConversionPreview").Text!.Contains("262144")) throw new Exception("FAIL: linear feedback preview incorrect: " + Control<TextBlock>("AxisConversionPreview").Text);
            Control<CheckBox>("ConfigureAxisMechanics").IsChecked = false;
            Control<TextBox>("AxisName").Text = "Legacy"; Control<TextBox>("ControllerAxis").Text = "Legacy1";
            window.SaveAxisProperties(false);
            var current = (StProject)typeof(MainWindow).GetField("project", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            if (current.Hardware.Axes.Single(a => a.Name == "Legacy").Mechanics is not null) throw new Exception("FAIL: legacy controller-only axis acquired unsupported mechanics");
            FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sources = ((Dictionary<Guid, SourceDocument>)Field("sources").GetValue(window)!).Values.ToArray();
            var snapshot = ProjectValidator.Capture(current, sources);
            Field("candidatePackage").SetValue(window, new PackageResult(new(true, [], "", "", "", ""), "fixture", new string('A', 64), ""));
            Field("candidateSourceHash").SetValue(window, snapshot.SourceHash); Field("candidateHardwareHash").SetValue(window, snapshot.HardwareHash);
            typeof(MainWindow).GetMethod("ApplyCompiledCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                [new DeploymentResponse(true, "compiled", "fixture", "", null, null, AxisReady: true)]);
            if (((Button)Field("activateButton").GetValue(window)!).IsEnabled || !Control<TextBlock>("BuildStatus").Text!.Contains("机械与回零参数尚未应用")) throw new Exception("FAIL: old device readiness can enable offline axis settings");
            Console.WriteLine("PASS axis hardware mechanical/rotary controls, conversion, rollback and save/reopen");
        }
        finally { window.Close(); }
    }
    private static void Pump(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult();
    }
}

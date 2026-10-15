using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using StWorkbench.App;
using StWorkbench.Core;

internal static class PathUiTests
{
    public static void Run(MainWindow window)
    {
        var project = (StProject)typeof(MainWindow).GetField("project", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        project.Hardware.Axes.AddRange([new("PathX", "PathX", "mm", -100, 100), new("PathY", "PathY", "mm", -100, 100)]);
        window.ImportPathText("test.dxf", "0\nSECTION\n2\nENTITIES\n0\nLINE\n8\ncut\n10\n0\n20\n0\n11\n5\n21\n0\n0\nENDSEC\n0\nEOF\n");
        var preview = new PathPreview();
        preview.SetPath([new("Arc", new(1, 0), new(0, 1), new(0, 0), Math.PI / 2)], new(10, 20));
        if (preview.Points.Count < 4 || preview.Points[0] != new PathPoint(11, 20)) throw new Exception("Path preview must preserve arcs and offsets");
        var instruction = MainWindow.PathInstruction(new MotionPathAsset { Name = "Contour", Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), Version = 7 });
        if (instruction.ReferenceOnly || !instruction.Code.Contains("Version := ULINT#7") || !instruction.Declarations.Any(d => d.Contains("MC_ExecuteContinuousPath"))) throw new Exception("Path resource must insert executable versioned ST");
        var simpleSource="PROGRAM Main\nVAR\nEND_VAR\nEND_PROGRAM\n";
        var named=StSnippetInsertion.Insert(simpleSource,simpleSource.IndexOf("END_PROGRAM",StringComparison.Ordinal),instruction);
        if(named.InstancePrefix!="Contour_1"||!named.Text.Contains("Contour_1 : MC_ExecuteContinuousPath;"))throw new Exception("FAIL: path instance must use readable path name");
        var repeated=StSnippetInsertion.Insert(named.Text,named.Text.IndexOf("END_PROGRAM",StringComparison.Ordinal),instruction);
        if(repeated.InstancePrefix!="Contour_2"||!repeated.Text.Contains("Version := ULINT#7"))throw new Exception("FAIL: path naming collision or version binding changed");
        try { window.AddPathAsset(new MotionPathAsset()); throw new Exception("Invalid path accepted"); } catch (InvalidDataException) { }
        if (window.FindControl<Button>("PathPrestore") is null || window.FindControl<TextBlock>("PathStatus") is null) throw new Exception("Path workspace controls missing");
        window.FindControl<TextBox>("PathScale")!.Text = "2";
        window.FindControl<TextBox>("PathOriginalUnit")!.Text = "custom-unit";
        window.PrestorePathDraft();
        var original = project.Paths.Single();
        if (MotionPathAsset.Validate(original).Count != 0) throw new Exception("Prestored asset hash invalid");
        if (original.ImportScale != 2 || original.OriginalUnit != "custom-unit" || original.Segments[0].End.X != 10) throw new Exception("Import parameters and scaled geometry must persist together");
        window.PrestorePathDraft();
        if (project.Paths.Count != 2 || project.Paths[1].Id != original.Id || project.Paths[1].Version != 2 || project.Paths[0].ContentHash != original.ContentHash) throw new Exception("New version must preserve old path hash and identity");
        if (project.Paths[1].Segments[0].End.X != 10) throw new Exception("Reopened normalized geometry must not scale twice");
        if (!MainWindow.PathInstruction(original).Code.Contains(original.ContentHash, StringComparison.Ordinal)) throw new Exception("ST must bind exact path hash");
        try { window.AddPathAsset(original); throw new Exception("Immutable version overwrite accepted"); } catch (InvalidDataException) { }
        try { window.AddPathAsset(original with { Version = 3, ContentHash = "tampered" }); throw new Exception("Tampered asset hash accepted"); } catch (InvalidDataException) { }
        try { window.AddPathAsset(original with { Id = Guid.NewGuid(), Version = 1, ContentHash = "" }); throw new Exception("Path name collision accepted"); } catch (InvalidDataException) { }
        if (project.Paths.Count != 2) throw new Exception("Failed prestore must roll back asset list");
        Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.GetFullPath("artifacts/st-workbench-paths.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        var editor = window.FindControl<AvaloniaEdit.TextEditor>("SourceEditor")!;
        var initialSource = editor.Text;
        window.InsertInstruction(MainWindow.PathInstruction(original).Id, editor.Text.IndexOf("END_PROGRAM", StringComparison.Ordinal));
        if(!editor.Text.Contains(original.Name+"_1 : MC_ExecuteContinuousPath;"))throw new Exception("FAIL: routed insertion did not use path name");
        if(Environment.GetEnvironmentVariable("ST_PATH_NAMING_COMPILE_VERIFY")=="1")
        {
            var build=window.GeneratePackageAsync();var deadline=DateTime.UtcNow.AddSeconds(30);
            while(!build.IsCompleted&&DateTime.UtcNow<deadline){Dispatcher.UIThread.RunJobs();Thread.Sleep(1);}
            if(!build.IsCompleted)throw new TimeoutException("Named path candidate timeout");build.GetAwaiter().GetResult();
            if(!window.FindControl<Button>("DownloadButton")!.IsEnabled)throw new Exception("FAIL: readable path instance did not compile/package");
            Console.WriteLine("PASS readable path instance real compilation and candidate package");
        }
        try { window.DeletePathAsset(original.Id, original.Version); throw new Exception("Referenced path deletion accepted"); } catch (InvalidDataException) { }
        window.DeletePathAsset(original.Id, 2);
        if (project.Paths.Any(p => p.Version == 2) || !project.Paths.Contains(original)) throw new Exception("Unreferenced version deletion must preserve referenced version");
        if (window.FindControl<TextBlock>("PathStatus")!.Text!.Contains("可连续运行")) throw new Exception("Offline path claims hardware readiness");
        editor.Text = initialSource; project.Paths.Clear(); project.Hardware.Axes.RemoveAll(a => a.Name is "PathX" or "PathY"); window.RefreshPaths();
    }
}

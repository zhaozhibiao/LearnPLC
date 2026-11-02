using StWorkbench.Core;

var temp = Path.Combine(Path.GetTempPath(), "st-core-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var main = new ProjectItem();
    var project = new StProject { Items = [main], Tasks = [new PlcTask { ProgramId = main.Id }] };
    var docs = new[] { new SourceDocument(main.Id, main.SourcePath, "PROGRAM Main\nVAR count : DINT; END_VAR\ncount := count + 1;\nEND_PROGRAM") };
    var store = new ProjectStore();
    var path = Path.Combine(temp, "Example.stproj");
    await store.SaveAsync(path, project, docs);
    var loaded = await store.LoadAsync(path);
    Check(loaded.Project.Id == project.Id && loaded.Sources[0].Text == docs[0].Text, "roundtrip");
    var snapshot = ProjectValidator.Capture(project, docs);
    Check(snapshot.SourceHash == ProjectValidator.Capture(project, docs).SourceHash, "stable hash");
    Check(snapshot.SourceHash != ProjectValidator.Capture(project, [docs[0] with { Text = docs[0].Text + "\n" }]).SourceHash, "changed hash");
    var original = await File.ReadAllTextAsync(path);
    main.SourcePath = "../escape.st";
    await Reject(() => store.SaveAsync(path, project, [docs[0] with { RelativePath = main.SourcePath }]));
    Check(await File.ReadAllTextAsync(path) == original, "failed save protects original");
    main.SourcePath = docs[0].RelativePath;
    project.Tasks.Add(new PlcTask { Name = "Other", ProgramId = main.Id });
    Check(ProjectValidator.Validate(project, docs).Any(d => d.Message.Contains("同一个程序")), "concurrent program binding rejection");
    project.Tasks.RemoveAt(1);
    project.Items.Add(new ProjectItem { Name = "main", SourcePath = "sources/Other.st" });
    Check(ProjectValidator.Validate(project, docs).Any(d => d.Message.Contains("重名")), "case insensitive collision");
    Console.WriteLine("PASS: persistence, immutable snapshot, change identity, traversal, failed save, tasks, duplicate names");
    TaskSchedulingTests.Run();
    await ConfigurationTests.Run();
    await CommunicationTests.RunAsync();
    LanguageTests.Run();
    InstructionTests.Run();
    ResourceInsertionTests.Run();
    await MotionCycleTests.Run();
    AxisBindingTests.Run();
    AxisMechanicsTests.Run();
    await PathAssetTests.Run();
    EsiTests.Run();
    EtherCatRuntimeTests.Run();
    IoAutoMappingTests.Run();
    await RegressionTests.Run();
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
finally { Directory.Delete(temp, true); }
return 0;

static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
static async Task Reject(Func<Task> operation) { try { await operation(); } catch (InvalidDataException) { return; } throw new Exception("FAIL: invalid save accepted"); }

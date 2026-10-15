using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using StWorkbench.Core;

public static class RegressionTests
{
    public static async Task Run()
    {
        var failures = new List<string>();
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS: " + name); }
            catch (Exception error) { failures.Add(name + ": " + error.Message); }
        }
        var temp = Path.Combine(Path.GetTempPath(), "st-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var item = new ProjectItem();
            var project = new StProject { Items = [item], Tasks = [new PlcTask { ProgramId = item.Id }] };
            var docs = new[] { new SourceDocument(item.Id, item.SourcePath, "PROGRAM Main END_PROGRAM") };
            var store = new ProjectStore(); var path = Path.Combine(temp, "project.stproj");
            await store.SaveAsync(path, project, docs);
            var original = await File.ReadAllTextAsync(path);
            await Test("snapshot task setters are immutable", () => {
                var snapshot = ProjectValidator.Capture(project, docs);
                foreach (var property in snapshot.Tasks[0].GetType().GetProperties())
                    Check(property.SetMethod is null || property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)), "Mutable snapshot property " + property.Name);
                project.Tasks[0].IntervalMilliseconds = 22;
                Check(snapshot.Tasks[0].IntervalMilliseconds == 10, "Project mutation changed snapshot");
                project.Tasks[0].IntervalMilliseconds = 10;
                return Task.CompletedTask;
            });
            await Test("save freezes metadata before asynchronous writes", async () => {
                var pending = store.SaveAsync(path, project, [docs[0] with { Text = docs[0].Text + new string(' ', 8 * 1024 * 1024) }]);
                Check(!pending.IsCompleted, "Regression needs a pending source write");
                project.Name = "Changed while saving";
                project.Tasks[0].IntervalMilliseconds = 99;
                item.Name = "Changed";
                await pending;
                var loaded = await store.LoadAsync(path);
                Check(loaded.Project.Name == "ST 工程" && loaded.Project.Items[0].Name == "Main" && loaded.Project.Tasks[0].IntervalMilliseconds == 10, "Saved mutable live metadata");
                project.Name = "ST 工程"; item.Name = "Main"; project.Tasks[0].IntervalMilliseconds = 10;
            });
            await Test("nested invalid manifests fail before reading sources", async () => {
                foreach (var mutate in new Action<JsonObject>[] {
                    json => json["Project"]!["Items"]![0] = null,
                    json => json["Project"]!["Tasks"]![0] = null,
                    json => json["Project"]!["Hardware"]!["Io"] = null,
                    json => json["Project"]!["Hardware"]!["Axes"] = new JsonArray((JsonNode?)null),
                    json => json["Project"]!["Items"]![0]!["Name"] = null,
                    json => json["Project"]!["Hardware"]!["Io"] = JsonNode.Parse("[{\"Name\":\"Input\",\"Address\":\"%IX0.0\",\"Type\":null,\"IsInput\":true}]"),
                    json => json["Project"]!["SchemaVersion"] = 999
                }) {
                    var json = JsonNode.Parse(original)!.AsObject(); mutate(json);
                    foreach (var key in json["SourceFiles"]!.AsObject().Select(p => p.Key).ToArray()) json["SourceFiles"]![key] = "missing.st";
                    await File.WriteAllTextAsync(path, json.ToJsonString());
                    try { await store.LoadAsync(path); throw new Exception("Invalid manifest accepted"); }
                    catch (InvalidDataException) { }
                }
            });
            await Test("direct validator rejects null nested members", () => {
                project.Items = [null!];
                Check(ProjectValidator.Validate(project, docs).Count > 0, "Null item must produce validation error");
                return Task.CompletedTask;
            });
            await Test("invalid hardware save returns validation error and protects manifest", async () => {
                project.Items = [item];
                project.Hardware.Axes = [new("Axis", "controller-axis", "mm", double.NaN, 100)];
                await File.WriteAllTextAsync(path, original);
                try { await store.SaveAsync(path, project, docs); throw new Exception("Invalid hardware accepted"); }
                catch (InvalidDataException) { }
                Check(await File.ReadAllTextAsync(path) == original, "Invalid save changed manifest");
            });
        }
        finally { Directory.Delete(temp, true); }
        if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

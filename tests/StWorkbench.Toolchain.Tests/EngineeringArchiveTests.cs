using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class EngineeringArchiveTests
{
    public static void Run()
    {
        var main = new ProjectItem();
        var data = new ProjectItem { Name = "Types", Kind = ItemKind.DataType, SourcePath = "sources/Types.st" };
        var project = new StProject { Items = [main, data], Tasks = [new() { ProgramId = main.Id }] };
        SourceDocument[] sources = [new(main.Id, main.SourcePath, "PROGRAM Main\nEND_PROGRAM"), new(data.Id, data.SourcePath, "TYPE Count : INT; END_TYPE")];
        var snapshot = ProjectValidator.Capture(project, sources);
        project.Items[1].Name = "Changed";
        var archive = EngineeringArchive.Create(snapshot);
        var loaded = EngineeringArchive.Read(JsonSerializer.Serialize(archive));
        if (loaded.Project.Items[1].Name != "Types" || loaded.Project.Items[1].Kind != ItemKind.DataType || loaded.Sources[1].Text != sources[1].Text)
            throw new Exception("Archive did not preserve original metadata/source");
        var bad = JsonSerializer.Serialize(archive).Replace("TYPE Count", "TYPE Tampered");
        try { EngineeringArchive.Read(bad); throw new Exception("Tampered archive accepted"); }
        catch (InvalidDataException) { }
        try { EngineeringArchive.Create(snapshot with { Tasks = [snapshot.Tasks[0] with { IntervalMilliseconds = 20 }] }); throw new Exception("Divergent snapshot tasks accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS engineering archive frozen metadata, original ST and identity verification");
    }
}

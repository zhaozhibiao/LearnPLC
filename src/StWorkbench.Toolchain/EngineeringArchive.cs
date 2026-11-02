using System.Text;
using System.Text.Json;
using StWorkbench.Core;

namespace StWorkbench.Toolchain;

public sealed record EngineeringArchive(int SchemaVersion, StProject Project, IReadOnlyList<SourceDocument> Sources, string SourceHash, string HardwareHash)
{
    public static EngineeringArchive Create(BuildSnapshot snapshot)
    {
        if (snapshot.ProjectJson is null) throw new InvalidDataException("This snapshot has no captured project metadata; capture the project again before packaging.");
        var project = JsonSerializer.Deserialize<StProject>(snapshot.ProjectJson) ?? throw new InvalidDataException("Empty captured project metadata.");
        var captured = ProjectValidator.Capture(project, snapshot.Sources);
        if (captured.SourceHash != snapshot.SourceHash || captured.HardwareHash != snapshot.HardwareHash)
            throw new InvalidDataException("Engineering archive differs from captured program identity.");
        if (JsonSerializer.Serialize(new { captured.Tasks, captured.Paths, captured.Axes, captured.MotionCycle, captured.Communication, captured.HardwareConfigurationJson }) !=
            JsonSerializer.Serialize(new { snapshot.Tasks, snapshot.Paths, snapshot.Axes, snapshot.MotionCycle, snapshot.Communication, snapshot.HardwareConfigurationJson }))
            throw new InvalidDataException("Engineering archive metadata differs from captured snapshot.");
        return new(1, project, snapshot.Sources.ToArray(), snapshot.SourceHash, snapshot.HardwareHash);
    }

    public static LoadedProject Read(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new InvalidDataException("Engineering archive exceeds capacity.");
        var archive = JsonSerializer.Deserialize<EngineeringArchive>(json) ?? throw new InvalidDataException("Empty engineering archive.");
        if (archive.SchemaVersion != 1) throw new InvalidDataException("Unsupported engineering archive schema.");
        var captured = ProjectValidator.Capture(archive.Project, archive.Sources);
        if (captured.HardwareHash != archive.HardwareHash ||
            captured.SourceHash != archive.SourceHash && !HasVerifiedLegacyTaskIdentity(json, archive, captured))
            throw new InvalidDataException("Engineering archive identity mismatch.");
        return new(archive.Project, archive.Sources);
    }

    // Archives preceding task scheduling hashed only these four task fields.
    // Accept that exact format only; explicit scheduling must use the new identity.
    private static bool HasVerifiedLegacyTaskIdentity(string json, EngineeringArchive archive, BuildSnapshot captured)
    {
        using var document = JsonDocument.Parse(json);
        var tasks = document.RootElement.GetProperty("Project").GetProperty("Tasks");
        if (tasks.EnumerateArray().Any(t => t.EnumerateObject().Any(p =>
            p.Name is not ("Name" or "ProgramId" or "IntervalMilliseconds" or "Enabled")))) return false;
        var legacyTasks = captured.Tasks.Select(t => new { t.Name, t.ProgramId, t.IntervalMilliseconds, t.Enabled }).ToArray();
        var identity = captured.Paths.Count == 0
            ? JsonSerializer.Serialize(new { archive.Project.Id, archive.Project.Name, Sources = captured.Sources, Tasks = legacyTasks })
            : JsonSerializer.Serialize(new { archive.Project.Id, archive.Project.Name, Sources = captured.Sources, Tasks = legacyTasks, Paths = captured.Paths });
        return ProjectValidator.Hash(identity) == archive.SourceHash;
    }
}

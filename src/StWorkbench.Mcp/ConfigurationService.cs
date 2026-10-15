using System.Text;
using StWorkbench.Core;
using StWorkbench.Motion;

namespace StWorkbench.Mcp;

public sealed class ConfigurationService(WorkspaceService workspace)
{
    public Task<WorkspaceView> ConfigureAsync(string session, string revision, HardwareConfiguration hardware,
        NetworkConfiguration? network, PlcTask[] tasks, MotionCycleConfiguration? motionCycle) =>
        workspace.EditAsync(session, revision, (project, _) =>
        { project.Hardware = McpJson.Clone(hardware); project.Network = McpJson.Clone(network); project.Tasks = McpJson.Clone(tasks).ToList(); project.MotionCycle = McpJson.Clone(motionCycle); });

    public async Task<WorkspaceView> ImportEsiAsync(string session, string revision, string path)
    {
        var full = workspace.Resolve(path);
        if (new FileInfo(full).Length > 8 * 1024 * 1024) throw new InvalidDataException("ESI exceeds 8 MiB.");
        using var stream = File.OpenRead(full);
        var devices = EsiImporter.Read(stream);
        return await workspace.EditAsync(session, revision, (project, _) => project.Hardware.Devices.AddRange(devices));
    }
    public async Task<object> AutoMapAsync(string session, string revision, Guid stationId)
    {
        IoAutoMappingPlan? plan = null;
        var draft = await workspace.EditAsync(session, revision, (project, _) =>
        {
            var station = project.Hardware.Stations?.SingleOrDefault(s => s.Id == stationId) ?? throw new ArgumentException("Unknown engineering station.");
            plan = IoAutoMapping.Plan(project.Hardware, station); project.Hardware.Io.AddRange(plan.Added);
        });
        return new { draft, added = plan!.Added, skipped = plan.Skipped };
    }
    public async Task<object> ImportDxfAsync(string session, string revision, string path, double millimetersPerUnit)
    {
        _ = await workspace.ReadAsync(session);
        var full = workspace.Resolve(path);
        if (new FileInfo(full).Length > DxfPathImporter.MaximumBytes) throw new InvalidDataException("DXF exceeds 8 MiB.");
        var text = await File.ReadAllTextAsync(full, new UTF8Encoding(false, true));
        var contours = DxfPathImporter.Parse(text, millimetersPerUnit);
        return new { revision, millimetersPerUnit, contours, notice = "Preview only. Prestore selected contour using path_store; coordinates are already in mm." };
    }
    public Task<WorkspaceView> StorePathAsync(string session, string revision, MotionPathAsset path) =>
        workspace.EditAsync(session, revision, (project, _) =>
        {
            var asset = McpJson.Clone(path).Seal();
            var prior = project.Paths.Where(p => p.Id == asset.Id).ToArray();
            if (prior.Length > 0 && (asset.Version <= prior.Max(p => p.Version) || prior.Any(p => p.Name != asset.Name))) throw new InvalidDataException("Path must append a newer version with the same resource name.");
            ContinuousPathPlanner.Prepare(asset, project.Hardware.Axes);
            project.Paths.Add(asset);
        });
    public async Task<object> PlanPathAsync(string session, Guid pathId, uint version)
    {
        var draft = await workspace.ReadAsync(session);
        var path = draft.Project.Paths.SingleOrDefault(p => p.Id == pathId && p.Version == version) ?? throw new ArgumentException("Unknown path ID/version.");
        var trajectory = ContinuousPathPlanner.Prepare(path, draft.Project.Hardware.Axes);
        return new { draft.Revision, path.Id, path.Version, path.ContentHash, trajectory.DurationSeconds, sampleCount = trajectory.Samples.Count,
            notice = "Offline planned duration; machine approach, waiting and controller differences are excluded. Physical continuous execution requires backend capability." };
    }
}

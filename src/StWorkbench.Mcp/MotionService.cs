using System.Collections.Concurrent;
using System.Text.Json;
using StWorkbench.Core;

namespace StWorkbench.Mcp;

public sealed record MotionPlan(string PlanId, string DeviceId, string SessionId, string Revision, string ManifestHash,
    string Axis, string Unit, string Kind, double Position, double Velocity, int DurationMilliseconds, DateTimeOffset ExpiresAt);

public sealed class MotionService(WorkspaceService workspace, ServerOptions options) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<IMotionTransport>> transports = new();
    private readonly ConcurrentDictionary<string, MotionPlan> plans = new();
    public Func<DeviceOptions, IMotionTransport>? TransportFactory { get; init; }
    private IMotionTransport Transport(string deviceId)
    {
        var device = options.Device(deviceId);
        return transports.GetOrAdd(deviceId, _ => new Lazy<IMotionTransport>(() => TransportFactory?.Invoke(device) ?? new MotionTransport(device))).Value;
    }
    public Task<JsonElement> StatusAsync(string deviceId) => RequestAsync(deviceId, "status");
    public Task<JsonElement> CapabilitiesAsync(string deviceId) => RequestAsync(deviceId, "capabilities");
    private async Task<WorkspaceView> RequireProject(string deviceId, string session, string revision)
    {
        var device = options.Device(deviceId);
        if (!device.AllowMotion) throw new InvalidOperationException("Motion control disabled in operator startup configuration.");
        var draft = await workspace.ReadAsync(session);
        if (draft.Revision != revision || draft.HasUnsavedChanges) throw new InvalidOperationException("Motion requires the current saved project revision.");
        await RequireSavedManifestAsync(workspace, draft);
        WorkspaceService.RequireValid(draft.Project, draft.Sources);
        if (draft.Project.Hardware.Axes.Any(a => a.Mechanics is not null)) throw new InvalidOperationException("capability_missing: applying mechanical/rotary parameters is not confirmed by this backend.");
        var response = await RequestAsync(deviceId, "capabilities");
        RequireResponse(response);
        var capabilities = response.GetProperty("capabilities");
        var actualBackend = capabilities.GetProperty("Backend").GetString();
        if (actualBackend != device.Backend || capabilities.GetProperty("ControllerReady").GetBoolean() != true ||
            !capabilities.GetProperty(device.Backend == "EcMotion" ? "PhysicalBasicAxis" : "SimulatorBasicAxis").GetBoolean()) throw new InvalidOperationException("capability_missing: configured backend is not ready for basic axis control.");
        if (!string.Equals(capabilities.GetProperty("ManifestSha256").GetString(), ManifestHash(draft.Project), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Axis mapping/limits manifest does not match this project. Install the verified manifest before control.");
        return draft;
    }
    public async Task<JsonElement> AcquireAsync(string device, string session, string revision, int durationMilliseconds)
    {
        _ = await RequireProject(device, session, revision);
        if (durationMilliseconds is < 250 or > 30000) throw new ArgumentException("Lease duration must be 250..30000 ms.");
        return await RequestAsync(device, "acquire", new Dictionary<string, object?> { ["durationMilliseconds"] = durationMilliseconds });
    }
    public Task<JsonElement> ReleaseAsync(string deviceId)
    {
        RequirePermission(deviceId); return RequestAsync(deviceId, "release");
    }
    public async Task<MotionPlan> PrepareAsync(string deviceId, string session, string revision, string axisName, string kind, double position, double velocity, int durationMilliseconds)
    {
        var draft = await RequireProject(deviceId, session, revision);
        var device = options.Device(deviceId);
        var axis = draft.Project.Hardware.Axes.SingleOrDefault(a => a.Name == axisName) ?? throw new ArgumentException("Unknown engineering axis.");
        if (!device.AllowedAxes.Contains(axisName, StringComparer.Ordinal)) throw new InvalidOperationException("Axis is outside the operator allowlist.");
        if (kind is not "Enable" and not "Disable" and not "Reset" and not "Home" and not "MoveAbsolute" and not "Jog") throw new ArgumentException("Unsupported motion kind. Use motion_stop for Stop.");
        if (device.Backend == "EcMotion" && (kind == "Jog" || kind is "Home" or "MoveAbsolute" &&
            new[] { axis.MaximumAcceleration, axis.MaximumDeceleration, axis.MaximumJerk }.Any(v => v.HasValue)))
            throw new InvalidOperationException("capability_missing: physical timed Jog and unconfirmed applied acceleration/deceleration/jerk limits require additional backend support.");
        if (!double.IsFinite(position) || !double.IsFinite(velocity)) throw new ArgumentException("Finite motion parameters required.");
        if (kind is "MoveAbsolute" or "Jog")
        {
            if (axis.MaximumVelocity is not { } cap || Math.Abs(velocity) > cap || velocity == 0 || kind == "MoveAbsolute" && velocity < 0) throw new ArgumentException("Configure an axis velocity cap, and use a nonzero velocity within it.");
            if (kind == "MoveAbsolute" && (position < axis.MinPosition || position > axis.MaxPosition)) throw new ArgumentException("Target exceeds engineering axis limits.");
        }
        if (kind == "Jog" && (durationMilliseconds is < 250 or > 30000)) throw new ArgumentException("Jog maximum duration must be 250..30000 ms.");
        if (plans.Count >= 64) foreach (var expired in plans.Where(p => p.Value.ExpiresAt < DateTimeOffset.UtcNow)) plans.TryRemove(expired.Key, out _);
        if (plans.Count >= 64) throw new InvalidOperationException("Pending motion plan capacity reached.");
        var plan = new MotionPlan(Guid.NewGuid().ToString("N"), deviceId, session, revision, ManifestHash(draft.Project), axisName, axis.Unit, kind, position, velocity, durationMilliseconds, DateTimeOffset.UtcNow.AddSeconds(20));
        plans[plan.PlanId] = plan; return plan;
    }
    public async Task<JsonElement> ExecuteAsync(string planId)
    {
        if (!plans.TryRemove(planId, out var plan)) throw new InvalidOperationException("Unknown/consumed motion plan. Commands are never replayed.");
        if (plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Motion plan expired.");
        var draft = await RequireProject(plan.DeviceId, plan.SessionId, plan.Revision);
        if (ManifestHash(draft.Project) != plan.ManifestHash) throw new InvalidOperationException("Axis mapping changed after planning.");
        return await RequestAsync(plan.DeviceId, "execute", new Dictionary<string, object?>
        {
            ["operationId"] = plan.PlanId, ["axis"] = plan.Axis, ["kind"] = plan.Kind, ["position"] = plan.Position,
            ["velocity"] = plan.Velocity, ["durationMilliseconds"] = plan.DurationMilliseconds
        });
    }
    public Task<JsonElement> StopAsync(string deviceId, string axis)
    {
        RequirePermission(deviceId);
        if (!options.Device(deviceId).AllowedAxes.Contains(axis, StringComparer.Ordinal)) throw new InvalidOperationException("Axis outside the operator allowlist.");
        return RequestAsync(deviceId, "stop", new Dictionary<string, object?> { ["axis"] = axis, ["operationId"] = Guid.NewGuid().ToString("N") });
    }
    public Task<JsonElement> ReadOperationAsync(string deviceId, string operationId) => RequestAsync(deviceId, "operation", new Dictionary<string, object?> { ["operationId"] = operationId });
    public async Task CloseAsync(string deviceId)
    {
        if (transports.TryRemove(deviceId, out var transport) && transport.IsValueCreated) await transport.Value.DisposeAsync();
        foreach (var plan in plans.Where(p => p.Value.DeviceId == deviceId)) plans.TryRemove(plan.Key, out _);
    }
    private void RequirePermission(string id) { if (!options.Device(id).AllowMotion) throw new InvalidOperationException("Motion control disabled by the operator."); }
    private async Task<JsonElement> RequestAsync(string deviceId, string action, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var response = await Transport(deviceId).RequestAsync(action, parameters);
        RequireResponse(response);
        return response;
    }
    internal static async Task RequireSavedManifestAsync(WorkspaceService workspace, WorkspaceView draft)
    {
        var saved = await new ProjectStore().LoadAsync(workspace.Resolve(draft.ProjectPath));
        var revision = ProjectValidator.Hash(JsonSerializer.Serialize(new { saved.Project, saved.Sources }, McpJson.Options));
        if (revision != draft.Revision) throw new InvalidOperationException("Saved project changed outside this session; reopen before device control.");
    }
    private static void RequireResponse(JsonElement response)
    {
        if (!response.GetProperty("success").GetBoolean()) throw new InvalidOperationException(response.TryGetProperty("message", out var message) ? message.GetString() : "Motion status could not be verified.");
    }
    public static string ManifestHash(StProject project) => ProjectValidator.Hash(ManifestJson(project));
    public static string ManifestJson(StProject project) => JsonSerializer.Serialize(new
    {
        SchemaVersion = 1, InterfaceVersion = "workbench-axis-v1", PlcIntervalMilliseconds = project.Tasks.Single(t => t.Enabled).IntervalMilliseconds,
        Axes = project.Hardware.Axes.OrderBy(a => a.Name, StringComparer.Ordinal).Select((a, i) => new Dictionary<string, object?>
        {
            ["Index"] = i + 1, ["Name"] = a.Name, ["ControllerAxis"] = a.ControllerAxis, ["Unit"] = a.Unit,
            ["MinPosition"] = a.MinPosition, ["MaxPosition"] = a.MaxPosition, ["MaximumVelocity"] = a.MaximumVelocity,
            ["MaximumAcceleration"] = a.MaximumAcceleration, ["MaximumDeceleration"] = a.MaximumDeceleration, ["MaximumJerk"] = a.MaximumJerk
        })
    });
    public async ValueTask DisposeAsync() { foreach (var id in transports.Keys) await CloseAsync(id); }
}

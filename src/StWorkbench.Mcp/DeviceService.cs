using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using StWorkbench.Toolchain;

namespace StWorkbench.Mcp;

public sealed record DevicePlan(string PlanId, string DeviceId, string Action, string SessionId, string Revision,
    string? BuildOperationId, string? BuildHash, string? ActiveBuildHash, bool WasRunning, bool RequiresCoreRestart, DateTimeOffset ExpiresAt);

public sealed class DeviceService(WorkspaceService workspace, ServerOptions options, BuildService builds, OperationJobs jobs)
{
    private readonly ConcurrentDictionary<string, DevicePlan> plans = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    // Test seam. Production always uses strict SSH with operator-provided known-hosts.
    public Func<DeviceOptions, DeploymentRequest, Task<DeploymentResponse>>? Transport { get; init; }
    private Task<DeploymentResponse> Send(DeviceOptions device, string action, string? buildHash = null, string? expected = null, string? package = null, string? packageHash = null)
    {
        jobs.ShutdownToken.ThrowIfCancellationRequested();
        var request = new DeploymentRequest(action, Guid.NewGuid().ToString("N"), buildHash, expected, package, packageHash);
        return Transport is null ? new RemoteDeploymentClient(knownHostsPath: device.KnownHostsPath).ExecuteAsync(device.Target, request, jobs.ShutdownToken) : Transport(device, request);
    }
    public async Task<object> StatusAsync(string id)
    {
        var device = options.Device(id); var response = await Send(device, "status");
        if (!response.Success) throw new InvalidOperationException("Device status failed: " + response.Message);
        return new { deviceId = id, response, notice = "Status does not acquire axis ownership or change PLC state." };
    }
    public async Task<DevicePlan> PrepareAsync(string deviceId, string action, string session, string revision, string? buildOperationId)
    {
        var device = options.Device(deviceId);
        if (!device.AllowPlcControl) throw new InvalidOperationException("PLC control disabled in operator startup configuration.");
        if (action is not "download" and not "start" and not "stop") throw new ArgumentException("Action must be download, start or stop.");
        var draft = await workspace.ReadAsync(session);
        if (draft.Revision != revision || draft.HasUnsavedChanges) throw new InvalidOperationException("Save the current project revision before device planning.");
        string? hash = null;
        if (action == "download")
        {
            var build = builds.Read(buildOperationId ?? "");
            if (build.SessionId != session || build.Revision != revision) throw new InvalidOperationException("Build does not match the saved project revision.");
            hash = builds.Package(buildOperationId!).BuildHash;
        }
        await MotionService.RequireSavedManifestAsync(workspace, draft);
        var status = await Send(device, "status");
        var baseline = ActiveHash(status);
        var plan = new DevicePlan(Guid.NewGuid().ToString("N"), deviceId, action, session, revision, buildOperationId, hash, baseline,
            status.State == "running", action == "download" && !string.Equals(hash, baseline, StringComparison.OrdinalIgnoreCase), DateTimeOffset.UtcNow.AddMinutes(2));
        if (plans.Count >= 64) foreach (var expired in plans.Where(p => p.Value.ExpiresAt < DateTimeOffset.UtcNow)) plans.TryRemove(expired.Key, out _);
        if (plans.Count >= 64) throw new InvalidOperationException("Pending plan capacity reached.");
        plans[plan.PlanId] = plan; return plan;
    }
    public OperationView Execute(string planId)
    {
        if (!plans.TryRemove(planId, out var plan)) throw new InvalidOperationException("Unknown or consumed device plan.");
        if (plan.ExpiresAt < DateTimeOffset.UtcNow) throw new InvalidOperationException("Device plan expired; prepare again.");
        return jobs.Start(() => ExecuteAsync(plan));
    }
    private async Task<object> ExecuteAsync(DevicePlan plan)
    {
        var device = options.Device(plan.DeviceId);
        var mutex = gates.GetOrAdd(device.Id, _ => new(1));
        if (!await mutex.WaitAsync(0)) throw new InvalidOperationException("Another PLC operation is in progress for this device; no command was sent.");
        try
        {
            await RequireRevision(plan);
            var current = await Send(device, "status");
            if (ActiveHash(current) != plan.ActiveBuildHash || (current.State == "running") != plan.WasRunning) throw new InvalidOperationException("Active PLC program/state changed since planning; prepare again.");
            if (plan.Action is "start" or "stop")
            {
                await RequireRevision(plan);
                var response = await Send(device, plan.Action, expected: plan.ActiveBuildHash);
                if (response.State == "unknown") return new { state = "Unknown", response, automaticRetry = false };
                if (!response.Success) throw new InvalidOperationException("PLC operation rejected: " + response.Message);
                var final = await Send(device, "status");
                if (ActiveHash(final) != plan.ActiveBuildHash || final.State != (plan.Action == "start" ? "running" : "stopped")) throw new InvalidOperationException("PLC result could not be verified; refresh status, do not replay automatically.");
                return new { state = "Completed", response = final };
            }
            var package = builds.Package(plan.BuildOperationId!);
            if (new FileInfo(package.PackagePath).Length > 32 * 1024 * 1024) throw new InvalidDataException("Package exceeds upload capacity.");
            var bytes = await File.ReadAllBytesAsync(package.PackagePath);
            var host = new DownloadHost(this, device, plan);
            var result = await ProgramDownloadWorkflow.ExecuteAsync(new(package.BuildHash, Convert.ToBase64String(bytes), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), host);
            return new { state = result.State.ToString(), result.Status, plan.WasRunning, notice = "Running/stopped state follows the verified original state. Unknown results are never retried." };
        }
        finally { mutex.Release(); }
    }
    private Task<WorkspaceView> ReadProjectAsync(string session) => workspace.ReadAsync(session);
    private async Task RequireRevision(DevicePlan plan)
    {
        var current = await workspace.ReadAsync(plan.SessionId);
        if (current.Revision != plan.Revision || current.HasUnsavedChanges) throw new InvalidOperationException("Project revision changed after device planning.");
        await MotionService.RequireSavedManifestAsync(workspace, current);
    }
    private static string? ActiveHash(DeploymentResponse response)
    {
        if (!response.Success || response.State is not "running" and not "stopped") throw new InvalidOperationException("Device state is not verified running/stopped: " + response.Message);
        if (response.Active is not { ValueKind: JsonValueKind.Object } active || !active.TryGetProperty("build_hash", out var value)) throw new InvalidOperationException("Active program identity unavailable.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        var text = value.GetString();
        if (text is not { Length: 64 } || !text.All(Uri.IsHexDigit)) throw new InvalidOperationException("Invalid active program identity.");
        return text.ToLowerInvariant();
    }
    private sealed class DownloadHost(DeviceService owner, DeviceOptions device, DevicePlan plan) : IProgramDownloadHost
    {
        private bool current = true;
        private bool ready;
        private string? verifiedActiveHash = plan.ActiveBuildHash;
        public bool IsCurrent => current;
        public bool CanApply => ready;
        public async Task<DeploymentResponse> SendAsync(string action, string? packageBase64 = null, string? packageHash = null)
        {
            try { await owner.RequireRevision(plan); }
            catch { current = false; throw; }
            var result = await owner.Send(device, action, plan.BuildHash, verifiedActiveHash, packageBase64, packageHash);
            try { await owner.RequireRevision(plan); } catch { current = false; throw; }
            if (result.State == "unknown") throw new IOException("PLC operation result unknown; refresh status without replay.");
            if (result.Success && result.State is "activated-not-running" or "running" or "stopped")
            {
                if (result.Active is { ValueKind: JsonValueKind.Object } active && active.TryGetProperty("build_hash", out var hash)) verifiedActiveHash = hash.ValueKind == JsonValueKind.Null ? null : hash.GetString();
            }
            return result;
        }
        public void ApplyCompilation(DeploymentResponse result)
        {
            var draft = owner.ReadProjectAsync(plan.SessionId).GetAwaiter().GetResult();
            ready = result.Success && draft.Project.Hardware.Axes.All(a => a.Mechanics is null) &&
                (draft.Project.Paths.Count == 0 || result.MotionReady == true) &&
                (draft.Project.MotionCycle?.Enabled != true || result.MotionCycleReady == true) &&
                (!StWorkbench.Core.AxisControlBinding.UsesAxisControl(draft.Sources) || result.AxisReady == true) &&
                (!StWorkbench.Core.CommunicationBinding.IsUsed(draft.Sources) || result.CommunicationReady == true);
        }
        public Task BeforeProgramChangeAsync() => owner.RequireRevision(plan);
        public Task<bool> ConfirmRestartAsync(ProgramRestartNotice notice) => Task.FromResult(plan.ExpiresAt > DateTimeOffset.UtcNow &&
            notice.WasRunning == plan.WasRunning && string.Equals(notice.ActiveBuildHash, plan.ActiveBuildHash, StringComparison.OrdinalIgnoreCase));
    }
}

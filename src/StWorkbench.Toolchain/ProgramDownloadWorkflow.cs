using System.Text.Json;

namespace StWorkbench.Toolchain;

public sealed record ProgramDownloadRequest(string BuildHash, string PackageBase64, string PackageSha256);
public sealed record ProgramRestartNotice(bool WasRunning, bool RequiresCoreRestart, string? ActiveBuildHash);
public enum ProgramDownloadState { Activated, AlreadyActive, Cancelled, Blocked }
public sealed record ProgramDownloadOutcome(ProgramDownloadState State, DeploymentResponse? Status = null);

public interface IProgramDownloadHost
{
    bool IsCurrent { get; }
    bool CanApply { get; }
    Task<DeploymentResponse> SendAsync(string action, string? packageBase64 = null, string? packageHash = null);
    void ApplyCompilation(DeploymentResponse result);
    Task<bool> ConfirmRestartAsync(ProgramRestartNotice notice);
    Task BeforeProgramChangeAsync();
}

public static class ProgramDownloadWorkflow
{
    public static async Task<ProgramDownloadOutcome> ExecuteAsync(ProgramDownloadRequest request, IProgramDownloadHost host)
    {
        var staged = await SendAsync(host, "stage", request.PackageBase64, request.PackageSha256);
        Require(staged.Success, "Package staging failed.");
        var compiled = await SendAsync(host, "compile");
        Require(compiled.Success && compiled.State == "compiled", "Compilation did not produce a verified candidate.");
        host.ApplyCompilation(compiled);
        EnsureCurrent(host);
        return await ApplyCompiledAsync(request.BuildHash, host);
    }

    public static async Task<ProgramDownloadOutcome> ApplyCompiledAsync(string buildHash, IProgramDownloadHost host)
    {
        EnsureCurrent(host);
        if (!host.CanApply) return new(ProgramDownloadState.Blocked);

        var initial = await SendAsync(host, "status");
        var baseline = ReadActiveHash(initial, "running", "stopped");
        if (SameHash(baseline, buildHash)) return new(ProgramDownloadState.AlreadyActive, initial);
        var wasRunning = initial.State == "running";
        var accepted = await host.ConfirmRestartAsync(new(wasRunning, true, baseline));
        EnsureCurrent(host);
        if (!accepted) return new(ProgramDownloadState.Cancelled, initial);

        var confirmed = await SendAsync(host, "status");
        var confirmedHash = ReadActiveHash(confirmed, "running", "stopped");
        Require(SameHash(baseline, confirmedHash) && confirmed.State == initial.State,
            "The active program or running state changed during confirmation; refresh status before applying.");
        if (!host.CanApply) return new(ProgramDownloadState.Blocked, confirmed);
        await host.BeforeProgramChangeAsync();
        EnsureCurrent(host);
        if (!host.CanApply) return new(ProgramDownloadState.Blocked, confirmed);

        if (wasRunning)
        {
            var stopped = await SendAsync(host, "stop");
            Require(SameHash(ReadActiveHash(stopped, "stopped"), baseline),
                "Stop did not verify the original program identity; refresh actual status.");
        }

        var activated = await SendAsync(host, "activate");
        Require(SameHash(ReadActiveHash(activated, "activated-not-running"), buildHash),
            "Activation did not verify the candidate identity; refresh actual status.");
        var final = await SendAsync(host, "status");
        Require(SameHash(ReadActiveHash(final, "stopped"), buildHash),
            "Final status did not verify the stopped candidate; refresh actual status.");
        if (wasRunning)
        {
            Require(host.CanApply, "The candidate is no longer ready; the new program remains stopped.");
            var started = await SendAsync(host, "start");
            Require(SameHash(ReadActiveHash(started, "running"), buildHash),
                "Resume did not verify the running candidate; refresh actual status before another action.");
            final = await SendAsync(host, "status");
            Require(SameHash(ReadActiveHash(final, "running"), buildHash),
                "Final status did not verify the resumed candidate; refresh actual status.");
        }
        return new(ProgramDownloadState.Activated, final);
    }

    private static async Task<DeploymentResponse> SendAsync(IProgramDownloadHost host, string action,
        string? packageBase64 = null, string? packageHash = null)
    {
        EnsureCurrent(host);
        var result = await host.SendAsync(action, packageBase64, packageHash);
        EnsureCurrent(host);
        return result;
    }

    private static string? ReadActiveHash(DeploymentResponse response, params string[] states)
    {
        Require(response.Success && states.Contains(response.State, StringComparer.Ordinal),
            "Device response did not verify the required state; refresh actual status. " + response.Message);
        if (response.Active is not { ValueKind: JsonValueKind.Object } active ||
            !active.TryGetProperty("build_hash", out var hash))
            throw new InvalidOperationException("The device has no registered active baseline.");
        if (hash.ValueKind == JsonValueKind.Null) return null;
        if (hash.ValueKind == JsonValueKind.String && hash.GetString() is { Length: 64 } value && value.All(Uri.IsHexDigit))
            return value;
        throw new InvalidOperationException("The device active build identity is invalid.");
    }

    private static bool SameHash(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static void EnsureCurrent(IProgramDownloadHost host) =>
        Require(host.IsCurrent, "The project, candidate or connection changed; refresh status before continuing.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Text.Json;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace StWorkbench.Toolchain;

public sealed record SshTarget(string Host, string User, int Port = 22);
public sealed record DeploymentRequest(string Action, string RequestId, string? BuildHash = null,
    string? ExpectedActiveBuildHash = null, string? PackageBase64 = null, string? PackageSha256 = null, string? PackagePath = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ConfigurationIdentity = null)
{
    public override string ToString() => $"DeploymentRequest {{ Action = {Action}, RequestId = {RequestId}, BuildHash = {BuildHash} }}";
}
public sealed record DeploymentResponse(bool Success, string State, string RequestId, string Message, JsonElement? Active, JsonElement? Candidate, string Log = "", bool? MotionReady = null, string MotionMessage = "", bool? MotionCycleReady = null, string MotionCycleMessage = "", JsonElement? Realtime = null, bool? AxisReady = null, string AxisMessage = "", bool? CommunicationReady = null, string CommunicationMessage = "", bool? HardwareMatchesActive = null, JsonElement? ConfigurationPlan = null, JsonElement? Transfer = null);

public sealed class RemoteDeploymentClient(string sshExecutable = "ssh", TimeSpan? timeout = null, string? knownHostsPath = null)
{
    public const string RemoteCommand = "/home/a/st-workbench-deployment/st-deployment-coordinator";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public async Task<DeploymentResponse> ExecuteAsync(SshTarget target, DeploymentRequest request, CancellationToken token = default)
    {
        if (!Regex.IsMatch(target.Host, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") || !Regex.IsMatch(target.User, @"^[A-Za-z_][A-Za-z0-9_-]{0,31}$") || target.Port is < 1 or > 65535)
            throw new ArgumentException("Invalid SSH host, user, or port.");
        if (!DeploymentActions.IsSupported(request.Action) || !Regex.IsMatch(request.RequestId, @"^[A-Za-z0-9_-]{1,80}$"))
            throw new ArgumentException("Unsupported action or request identity.");
        if (request.PackageBase64?.Length > 44739248) throw new ArgumentException("Package exceeds transport limit.");
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(6));
        var start = new ProcessStartInfo(sshExecutable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10" }) start.ArgumentList.Add(argument);
        if (knownHostsPath is not null)
        {
            if (knownHostsPath.Contains('\r') || knownHostsPath.Contains('\n')) throw new ArgumentException("Invalid known-hosts file path.");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("UserKnownHostsFile=" + Path.GetFullPath(knownHostsPath));
        }
        foreach (var argument in new[] { "-p", target.Port.ToString(), "--", target.User + "@" + target.Host, RemoteCommand }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        Task<string>? output = null;
        Task<string>? errors = null;
        var started = false;
        DeploymentResponse Unknown(string message) => new(false, "unknown", request.RequestId, message, null, null);
        try
        {
            process.Start();
            started = true;
            output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            errors = ReadBoundedAsync(process.StandardError, deadline.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var body = await output;
            _ = await errors;
            using var response = JsonDocument.Parse(body);
            var root = response.RootElement;
            if (root.GetProperty("request_id").GetString() != request.RequestId) return Unknown("Remote response identity does not match this request; query status.");
            var success = root.GetProperty("success").GetBoolean();
            var state = root.GetProperty("state").GetString() ?? "unknown";
            var message = root.TryGetProperty("message", out var detail) ? detail.GetString() ?? "" : "";
            JsonElement? active = root.TryGetProperty("active", out var activeValue) ? activeValue.Clone() : null;
            JsonElement? candidate = root.TryGetProperty("candidate", out var candidateValue) ? candidateValue.Clone() : null;
            if (process.ExitCode != 0 && success) return Unknown("SSH exit status contradicts the response; query status.");
            var log = root.TryGetProperty("log", out var logValue) ? logValue.GetString() ?? "" : "";
            bool? motionReady = root.TryGetProperty("motion_ready", out var motion) ? motion.GetBoolean() : null;
            var motionMessage = root.TryGetProperty("motion_message", out var motionDetail) ? motionDetail.GetString() ?? "" : "";
            bool? cycleReady = root.TryGetProperty("motion_cycle_ready", out var cycle) ? cycle.GetBoolean() : null;
            var cycleMessage = root.TryGetProperty("motion_cycle_message", out var cycleDetail) ? cycleDetail.GetString() ?? "" : "";
            JsonElement? realtime = root.TryGetProperty("realtime", out var rt) ? rt.Clone() : null;
            bool? axisReady = root.TryGetProperty("axis_ready", out var axis) ? axis.GetBoolean() : null;
            var axisMessage = root.TryGetProperty("axis_message", out var axisDetail) ? axisDetail.GetString() ?? "" : "";
            bool? communicationReady = root.TryGetProperty("communication_ready", out var comm) ? comm.GetBoolean() : null;
            var communicationMessage = root.TryGetProperty("communication_message", out var commDetail) ? commDetail.GetString() ?? "" : "";
            bool? hardwareMatches = root.TryGetProperty("hardware_matches_active", out var hardware) ? hardware.GetBoolean() : null;
            JsonElement? configurationPlan = root.TryGetProperty("configuration_plan", out var plan) ? plan.Clone() : null;
            JsonElement? transfer = root.TryGetProperty("transfer", out var transferValue) ? transferValue.Clone() : null;
            return new(success, state, request.RequestId, message, active, candidate, log, motionReady, motionMessage, cycleReady, cycleMessage, realtime, axisReady, axisMessage, communicationReady, communicationMessage, hardwareMatches, configurationPlan, transfer);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Unknown("Remote request timed out; query actual state before another action."); }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Unknown("Remote result could not be verified: " + error.Message); }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
            deadline.Cancel();
            foreach (var pending in new[] { output, errors }) if (pending is not null)
                try { await pending; } catch (OperationCanceledException) { } catch (IOException) { } catch (InvalidOperationException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + count > 2097152) throw new InvalidOperationException("SSH response exceeds capacity.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

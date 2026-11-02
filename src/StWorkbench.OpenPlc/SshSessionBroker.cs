using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace StWorkbench.OpenPlc;
public sealed class SshSessionBroker
{
    public async Task<string> AuthenticateAsync(string host, string user, string knownHostsPath, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") || !Regex.IsMatch(user, @"^[A-Za-z_][A-Za-z0-9_-]{0,31}$")) throw new ArgumentException("Invalid SSH target.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var start = new ProcessStartInfo("ssh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=8", "-o", "UserKnownHostsFile=" + Path.GetFullPath(knownHostsPath), "--", user + "@" + host, "/home/a/st-workbench-runtime-install/st-session-helper --runtime-session" }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start }; var started = false;
        async Task<string> Read(StreamReader reader)
        {
            var result = new System.Text.StringBuilder(); var buffer = new char[1024]; int length;
            while ((length = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0) { if (result.Length + length > 16384) throw new InvalidDataException("Private session response exceeds limit."); result.Append(buffer, 0, length); }
            return result.ToString();
        }
        Task<string>? output = null, errors = null;
        try
        {
            started = process.Start(); output = Read(process.StandardOutput); errors = Read(process.StandardError);
            await process.WaitForExitAsync(deadline.Token); var response = await output; _ = await errors;
            if (process.ExitCode != 0) throw new OpenPlcException("Private SSH authentication unavailable.");
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String) throw new OpenPlcException("Private SSH authentication unavailable.");
            var value = token.GetString()!;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 12000 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~' or '+' or '/' or '='))) throw new OpenPlcException("Invalid private session.");
            return value;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OpenPlcException) { throw new OpenPlcException("Private SSH authentication unavailable."); }
        finally
        {
            if (started) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
            deadline.Cancel();
            foreach (var pending in new[] { output, errors }) if (pending is not null) try { await pending; } catch (Exception) { }
        }
    }
}

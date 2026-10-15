using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StWorkbench.Mcp;

public interface IMotionTransport : IAsyncDisposable
{
    Task<JsonElement> RequestAsync(string action, IReadOnlyDictionary<string, object?>? parameters = null);
}

public sealed class MotionTransport(DeviceOptions device, string sshExecutable = "ssh") : IMotionTransport
{
    private readonly SemaphoreSlim gate = new(1);
    private Process? process;
    private Task? drain;
    private volatile bool failed;
    private readonly object lifecycle = new();
    public async Task<JsonElement> RequestAsync(string action, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (action is "stop" or "release")
        {
            if (!await gate.WaitAsync(TimeSpan.FromMilliseconds(250), deadline.Token))
            {
                Abort();
                throw new IOException("Stop/release could not pass a blocked motion exchange. Session closed for device-side watchdog teardown; actual stop is UNKNOWN. No replay.");
            }
        }
        else await gate.WaitAsync(deadline.Token);
        try
        {
            if (failed) throw new IOException("Motion session disconnected; no automatic reconnect or replay. Close the session explicitly before reconnecting.");
            if (process is null) Start();
            var request = parameters is null ? new Dictionary<string, object?>() : new(parameters);
            var id = Guid.NewGuid().ToString("N"); request["requestId"] = id; request["action"] = action;
            var line = JsonSerializer.Serialize(request);
            if (Encoding.UTF8.GetByteCount(line) >= 65536) throw new ArgumentException("Motion request exceeds capacity.");
            await process!.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            var response = await ReadLineBounded(process.StandardOutput, deadline.Token);
            var document = JsonSerializer.Deserialize<JsonElement>(response, McpJson.Options);
            if (document.GetProperty("requestId").GetString() != id || document.GetProperty("success").ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw new InvalidDataException("Motion response identity/schema mismatch.");
            return document;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or JsonException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            Abort();
            throw new IOException("Motion result unknown; never replay automatically. Refresh actual device status. " + error.Message, error);
        }
        finally { gate.Release(); }
    }
    private void Start()
    {
        lock (lifecycle)
        {
        if (failed) throw new IOException("Motion session already closed.");
        device.Validate();
        var start = new ProcessStartInfo(sshExecutable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "UserKnownHostsFile=" + device.KnownHostsPath, "-p", device.Port.ToString(), "--", device.User + "@" + device.Host,
            "python3 " + device.GatewayPath + " --manifest " + device.AxisManifestPath + " --controller-socket " + device.ControllerSocket + " --backend " + device.Backend }) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new IOException("Could not start SSH motion session.");
        drain = Drain(process.StandardError);
        }
    }
    private void Abort()
    {
        lock (lifecycle)
        {
            failed = true;
            if (process is not null)
            {
                // Kill first: closing a buffered writer must not wait for a blocked pipe flush.
                try { if (!process.HasExited) process.Kill(true); }
                catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                try { process.StandardInput.Close(); }
                catch (IOException) { } catch (InvalidOperationException) { }
            }
        }
    }
    private static async Task Drain(StreamReader reader)
    {
        var buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer.AsMemory()) > 0) { } } catch (IOException) { } catch (ObjectDisposedException) { }
    }
    private static async Task<string> ReadLineBounded(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) > 0)
        {
            if (buffer[0] == '\n') return result.ToString();
            if (result.Length >= 65536) throw new InvalidDataException("Motion response exceeds capacity.");
            result.Append(buffer[0]);
        }
        throw new IOException("Motion gateway closed before a complete response.");
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            failed = true;
            if (process is null) return;
            try
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); }
                if (drain is not null) await drain;
            }
            finally { process.Dispose(); process = null; }
        }
        finally { gate.Release(); }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
namespace StWorkbench.OpenPlc;

/// <summary>Owns loopback forwarding; never reuses another application's listener.</summary>
public sealed class SshRuntimeTunnel : IDisposable
{
    private readonly Process process;
    private bool disposed;
    public Uri Endpoint { get; }
    private SshRuntimeTunnel(Process process, int port)
    { this.process = process; Endpoint = new Uri($"https://localhost:{port}/"); }

    public static async Task<SshRuntimeTunnel> ConnectAsync(string host, string user, string knownHostsPath,
        string certificateFingerprint, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") ||
            !Regex.IsMatch(user, @"^[A-Za-z_][A-Za-z0-9_-]{0,31}$")) throw new ArgumentException("Invalid SSH target.");
        var pin = certificateFingerprint.Replace(":", "", StringComparison.Ordinal);
        if (pin.Length != 64 || pin.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("请先配置已核对的设备证书 SHA256。");
        if (!File.Exists(knownHostsPath)) throw new FileNotFoundException("缺少已核对的 SSH 主机身份文件。");
        cancellationToken.ThrowIfCancellationRequested();
        // A bind race fails closed through ExitOnForwardFailure; readiness requires pinned HTTPS.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        int port;
        try { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
        var start = new ProcessStartInfo("ssh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-N", "-L", $"127.0.0.1:{port}:127.0.0.1:8443", "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=yes", "-o", "ExitOnForwardFailure=yes", "-o", "ConnectTimeout=8",
            "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=2", "-o",
            "UserKnownHostsFile=" + Path.GetFullPath(knownHostsPath), "--", user + "@" + host }) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        var tunnel = new SshRuntimeTunnel(process, port);
        try
        {
            process.Start();
            _ = DrainAsync(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) => DeviceCertificateTrust.Validate(cert, errors, pin);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            using var probe = new OpenPlcClient(tunnel.Endpoint, http);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new OpenPlcException("SSH 隧道建立失败，请检查密钥、主机身份和网络。");
                try
                {
                    await probe.GetVersionAsync(deadline.Token).ConfigureAwait(false);
                    if (process.HasExited) throw new OpenPlcException("SSH 隧道已断开。");
                    return tunnel;
                }
                catch (OpenPlcException) { await Task.Delay(150, deadline.Token).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { tunnel.Dispose(); throw new OpenPlcException("SSH 隧道或 Runtime HTTPS 未就绪，请检查服务和设备证书。"); }
        catch { tunnel.Dispose(); throw; }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        try { var buffer = new char[1024]; while (await reader.ReadAsync(buffer) > 0) { } }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(2000); } }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}

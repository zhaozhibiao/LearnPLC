using System.Net.WebSockets;
using System.Buffers.Binary;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
namespace StWorkbench.OpenPlc;

public sealed record DebugProgramIdentity(string Md5, bool LittleEndian);
public sealed record DebugVariableAddress(byte ArrayIndex, ushort ElementIndex, ushort ByteSize);
public sealed record DebugVariableValue(DebugVariableAddress Address, ReadOnlyMemory<byte> Bytes);
public sealed record DebugReadBatch(uint Tick, IReadOnlyList<DebugVariableValue> Values);

public sealed class DebugSocketClient : IAsyncDisposable
{
    private const string Namespace = "/api/debug";
    private const string EventPrefix = "42/api/debug,";
    private const int MaximumPacketBytes = 16 * 1024;
    private readonly WebSocket socket;
    private readonly Uri socketUri;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operations = new(1, 1), sends = new(1, 1);
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<byte[]>? pending;
    private Task? reader;
    private Exception? failure;
    private TimeSpan heartbeatTimeout = TimeSpan.FromSeconds(90);
    private bool started, disposed;
    private volatile bool authenticated;

    // The supplied test transport must already be open; real connections always use WSS.
    public DebugSocketClient(Uri endpoint, string? certificateSha256 = null, TimeSpan? timeout = null, WebSocket? transport = null, string? engineeringToken = null)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || endpoint.AbsolutePath != "/" ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("Debug endpoint must be an HTTPS origin.", nameof(endpoint));
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
        socketUri = new UriBuilder(endpoint) { Scheme = "wss", Path = "/socket.io/", Query = "EIO=4&transport=websocket" }.Uri;
        if (transport is not null) socket = transport;
        else
        {
            var client = new ClientWebSocket();
            if (engineeringToken is not null) client.Options.SetRequestHeader("X-Engineering-Token", engineeringToken);
            if (endpoint.IsLoopback) client.Options.Proxy = null;
            if (!string.IsNullOrWhiteSpace(certificateSha256))
                client.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    using var cert = certificate is null ? null : new X509Certificate2(certificate);
                    return DeviceCertificateTrust.Validate(cert, errors, certificateSha256);
                };
            socket = client;
        }
    }
    public bool IsConnected => authenticated && !disposed && socket.State == WebSocketState.Open;

    public async Task ConnectAsync(string token, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) throw new ArgumentException("A bounded JWT is required.", nameof(token));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(timeout);
        await operations.WaitAsync(deadline.Token);
        try
        {
            if (started) throw new InvalidOperationException("Create a new debug client to reconnect.");
            started = true;
            if (socket is ClientWebSocket client) await client.ConnectAsync(socketUri, deadline.Token);
            if (socket.State != WebSocketState.Open) throw new IOException("Debug socket is not open.");
            var opening = await ReceivePacketAsync(deadline.Token);
            if (!opening.StartsWith('0')) throw new InvalidDataException("Missing EngineIO opening packet.");
            using var json = JsonDocument.Parse(opening[1..], new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (string.IsNullOrWhiteSpace(root.GetProperty("sid").GetString())) throw new InvalidDataException("Missing EngineIO session identity.");
            var heartbeatMs = checked(root.GetProperty("pingInterval").GetInt32() + root.GetProperty("pingTimeout").GetInt32());
            if (heartbeatMs < 1 || heartbeatMs > 120000) throw new InvalidDataException("Unsupported heartbeat timeout.");
            heartbeatTimeout = TimeSpan.FromMilliseconds(heartbeatMs);
            reader = ReceiveLoopAsync();
            await SendPacketAsync("40" + Namespace + "," + JsonSerializer.Serialize(new { token }), deadline.Token);
            await connected.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !disposed && Volatile.Read(ref failure) is not null)
        { ExceptionDispatchInfo.Capture(Volatile.Read(ref failure)!).Throw(); throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !disposed)
        { Fail(new TimeoutException("Debug connection timed out.")); throw new TimeoutException("Debug connection timed out."); }
        catch (Exception error) { Fail(error); throw; }
        finally { operations.Release(); }
    }

    public async Task<DebugProgramIdentity> GetProgramMd5Async(CancellationToken cancellationToken = default)
    {
        var data = await RequestAsync([0x45, 0xde, 0xad, 0, 0], cancellationToken);
        if (data.Length != 36 || data[0] != 0x45 || data[1] != 0x7e) throw ProtocolError("Invalid MD5 response or program not loaded.");
        var md5 = Encoding.ASCII.GetString(data, 2, 32);
        if (md5.Any(c => !Uri.IsHexDigit(c))) throw ProtocolError("Invalid program MD5.");
        var little = data[34] == 0xad && data[35] == 0xde;
        if (!little && !(data[34] == 0xde && data[35] == 0xad)) throw ProtocolError("Invalid target endianness sentinel.");
        return new(md5.ToLowerInvariant(), little);
    }

    public async Task<IReadOnlyList<ushort>> GetVariableCountsAsync(CancellationToken cancellationToken = default)
    {
        var data = await RequestAsync([0x41], cancellationToken);
        if (data.Length < 3 || data[0] != 0x41 || data[2] != 0x7e || data.Length != 3 + data[1] * 2)
            throw ProtocolError("Invalid variable array metadata or program not loaded.");
        var counts = new ushort[data[1]];
        for (var i = 0; i < counts.Length; i++) counts[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(3 + i * 2, 2));
        return Array.AsReadOnly(counts);
    }

    // ByteSize must come from the matched compiled debug-map, never guessed from display text.
    // Unsupported/skipped variables or truncated batches are rejected, never filled with zeros.
    public async Task<DebugReadBatch> ReadVariablesAsync(IReadOnlyList<DebugVariableAddress> variables, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var addresses = variables.ToArray();
        if (addresses.Length is < 1 or > 1024 || addresses.Any(a => a is null || a.ByteSize == 0)) throw new ArgumentException("Read requires 1–1024 addresses with known nonzero sizes.", nameof(variables));
        var expectedBytes = addresses.Sum(a => (int)a.ByteSize);
        if (expectedBytes > 4086) throw new ArgumentException("Split reads into batches smaller than the 4096-byte PDU limit.", nameof(variables));
        var pdu = new byte[3 + addresses.Length * 3]; pdu[0] = 0x44;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)addresses.Length);
        for (var i = 0; i < addresses.Length; i++) {
            pdu[3 + i * 3] = addresses[i].ArrayIndex;
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4 + i * 3, 2), addresses[i].ElementIndex);
        }
        var data = await RequestAsync(pdu, cancellationToken);
        if (data.Length < 10 || data[0] != 0x44 || data[1] != 0x7e ||
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2)) != addresses.Length - 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8, 2)) != expectedBytes || data.Length != expectedBytes + 10)
            throw ProtocolError("Read batch is incomplete or does not match debug-map sizes.");
        var values = new DebugVariableValue[addresses.Length]; var offset = 10;
        for (var i = 0; i < addresses.Length; i++) {
            values[i] = new(addresses[i], data.AsMemory(offset, addresses[i].ByteSize)); offset += addresses[i].ByteSize;
        }
        return new(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)), Array.AsReadOnly(values));
    }

    // One-shot soft write. FC42 forcing is deliberately not exposed.
    // Success means queued at the dispatcher boundary; readback is separate.
    public async Task WriteBooleanAsync(string expectedProgramMd5, DebugVariableAddress address, bool value, CancellationToken cancellationToken = default)
    {
        if (expectedProgramMd5 is null || expectedProgramMd5.Length != 32 || expectedProgramMd5.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A matched program MD5 is required.", nameof(expectedProgramMd5));
        if (address is null || address.ByteSize != 1) throw new ArgumentException("BOOL writes require the matched one-byte debug address.", nameof(address));
        var capability = await RequestAsync([0x46, 0], cancellationToken);
        if (!capability.AsSpan().SequenceEqual(new byte[] { 0x46, 0x7e, 1 }))
            throw ProtocolError("Runtime does not support identity-bound single writes; no force fallback is allowed.");
        var pdu = new byte[38]; pdu[0] = 0x46; pdu[1] = 1;
        Encoding.ASCII.GetBytes(expectedProgramMd5.ToLowerInvariant()).CopyTo(pdu, 2);
        pdu[34] = address.ArrayIndex;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(35, 2), address.ElementIndex);
        pdu[37] = value ? (byte)1 : (byte)0;
        var result = await RequestAsync(pdu, cancellationToken);
        if (!result.AsSpan().SequenceEqual(new byte[] { 0x46, 0x7e }))
            throw ProtocolError(result.Length == 2 && result[0] == 0x46 && result[1] == 0x84
                ? "Program identity changed; the single write was rejected. Rebind monitoring."
                : result.Length == 2 && result[0] == 0x46 && result[1] == 0x83
                ? "PLC 未运行或程序未加载，单次写入未提交；请先运行 PLC 并重新绑定监视。"
                : "Runtime did not acknowledge the single write; query actual value before any further action.");
    }

    private async Task<byte[]> RequestAsync(byte[] pdu, CancellationToken cancellationToken)
    {
        // No public raw PDU or force API. Only read operations and the bounded FC46 helper above.
        if (pdu[0] is not (0x41 or 0x44 or 0x45 or 0x46)) throw new InvalidOperationException("Unsupported debug operation.");
        ObjectDisposedException.ThrowIf(disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(timeout);
        var acquired = false;
        try
        {
            await operations.WaitAsync(deadline.Token); acquired = true;
            if (!IsConnected) throw new IOException("Debug connection unavailable; reconnect and reverify program identity.");
            // Upstream has no request IDs. Exactly one outstanding request; on timeout
            // abort the transport so a late response cannot bind to a later request.
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref pending, completion);
            var command = string.Join(" ", pdu.Select(b => b.ToString("X2")));
            await SendPacketAsync(EventPrefix + JsonSerializer.Serialize(new object[] { "debug_command", new { command } }), deadline.Token);
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !disposed && Volatile.Read(ref failure) is not null)
        { ExceptionDispatchInfo.Capture(Volatile.Read(ref failure)!).Throw(); throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !disposed)
        { Fail(new TimeoutException("Debug request timed out; outcome may be unknown. Reconnect before further operations.")); throw new TimeoutException("Debug request timed out; outcome may be unknown. Reconnect before further operations."); }
        catch (Exception error) { if (acquired) Fail(error); throw; }
        finally { if (acquired) { Volatile.Write(ref pending, null); operations.Release(); } }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); heartbeat.CancelAfter(heartbeatTimeout);
                var packet = await ReceivePacketAsync(heartbeat.Token);
                if (packet == "2") { await SendPacketAsync("3", lifetime.Token); continue; }
                if (packet.StartsWith("40" + Namespace + ",", StringComparison.Ordinal))
                {
                    using var acknowledgement = JsonDocument.Parse(packet[(3 + Namespace.Length)..], new JsonDocumentOptions { MaxDepth = 16 });
                    if (!acknowledgement.RootElement.TryGetProperty("sid", out var sid) || sid.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sid.GetString()))
                        throw new InvalidDataException("Missing SocketIO namespace session identity.");
                    authenticated = true; connected.TrySetResult(); continue;
                }
                if (packet.StartsWith("44" + Namespace, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Debug namespace authentication rejected.");
                if (packet is "1" || packet.StartsWith("41" + Namespace, StringComparison.Ordinal)) throw new IOException("Debug server disconnected.");
                if (!packet.StartsWith(EventPrefix, StringComparison.Ordinal)) throw new InvalidDataException("Unsupported EngineIO/SocketIO packet.");
                using var json = JsonDocument.Parse(packet[EventPrefix.Length..], new JsonDocumentOptions { MaxDepth = 16 });
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2) throw new InvalidDataException("Malformed debug event.");
                var name = root[0].GetString();
                if (name == "connected") continue;
                if (name != "debug_response") throw new InvalidDataException("Unexpected debug event.");
                var completion = Volatile.Read(ref pending) ?? throw new InvalidDataException("Uncorrelated debug response.");
                if (!root[1].GetProperty("success").GetBoolean()) throw new IOException("Runtime rejected the debug request.");
                var hex = root[1].GetProperty("data").GetString() ?? throw new InvalidDataException("Empty debug data.");
                var words = hex.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length is < 1 or > 4096 || words.Any(w => w.Length != 2 || w.Any(c => !Uri.IsHexDigit(c)))) throw new InvalidDataException("Malformed or oversized debug PDU.");
                if (!completion.TrySetResult(words.Select(w => Convert.ToByte(w, 16)).ToArray())) throw new InvalidDataException("Duplicate debug response.");
            }
        }
        catch (Exception error) { if (!disposed) Fail(error); }
    }

    private async Task<string> ReceivePacketAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumPacketBytes]; var offset = 0;
        while (true)
        {
            if (offset == buffer.Length) throw new InvalidDataException("Oversized debug frame.");
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, buffer.Length - offset), cancellationToken);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Debug socket closed or sent an unsupported binary packet.");
            offset += result.Count;
            if (result.EndOfMessage) return new UTF8Encoding(false, true).GetString(buffer, 0, offset);
        }
    }
    private async Task SendPacketAsync(string packet, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(packet);
        if (bytes.Length > MaximumPacketBytes) throw new InvalidDataException("Oversized outgoing debug packet.");
        await sends.WaitAsync(cancellationToken);
        try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken); }
        finally { sends.Release(); }
    }
    private InvalidDataException ProtocolError(string message) { var error = new InvalidDataException(message); Fail(error); return error; }
    private void Fail(Exception error)
    {
        authenticated = false;
        Interlocked.CompareExchange(ref failure, error, null);
        // Publish the actual protocol/auth error before canceling linked waits.
        connected.TrySetException(error); Volatile.Read(ref pending)?.TrySetException(error);
        socket.Abort(); lifetime.Cancel();
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; authenticated = false;
        lifetime.Cancel(); socket.Abort(); Volatile.Read(ref pending)?.TrySetCanceled(); connected.TrySetCanceled();
        if (reader is not null) await reader;
        socket.Dispose();
        // Semaphores/CTS can still be used by a canceled operation's finally block;
        // leaving these small managed objects to GC avoids disposal races.
    }
}

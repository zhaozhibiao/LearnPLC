using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StWorkbench.OpenPlc;

try
{
    var hashWire = new byte[127]; hashWire[0] = 64; System.Text.Encoding.UTF8.GetBytes(new string('a', 64)).CopyTo(hashWire, 1);
    Check(DebugValueDecoder.Decode("STRING", hashWire, true) == new string('a', 64), "Pinned length-prefixed path hash string");
    hashWire[0] = 127; Check(DebugValueDecoder.Decode("STRING", hashWire, true) is null, "String wire capacity is enforced");
    var wideWire = new byte[253]; wideWire[0] = 2; System.Text.Encoding.Unicode.GetBytes("轴组").CopyTo(wideWire, 1);
    Check(DebugValueDecoder.Decode("WSTRING", wideWire, false) == "轴组", "Pinned WSTRING wire is UTF16LE independent of device byte order");
    if (Environment.GetEnvironmentVariable("ST_REAL_DEBUG_VERIFY") == "1")
    {
        var package = Environment.GetEnvironmentVariable("ST_REAL_PACKAGE_PATH") ?? throw new Exception("Missing real package path");
        using var zip = System.IO.Compression.ZipFile.OpenRead(package);
        using var source = zip.GetEntry("debug-map.json")!.Open(); using var map = await JsonDocument.ParseAsync(source);
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var token = await new SshSessionBroker().AuthenticateAsync("192.168.1.31", "a", Path.Combine(workspace, "artifacts", "ssh-known-hosts"));
        await using var real = new DebugSocketClient(new Uri("https://localhost:18443"), "246b1274cbc242f060c0ba098fda3aac05ac873b561f765967a4e81e22caa9c4");
        await real.ConnectAsync(token); var identity = await real.GetProgramMd5Async();
        Check(identity.Md5 == map.RootElement.GetProperty("md5").GetString(), "Real loaded program identity");
        var leaves = map.RootElement.GetProperty("leaves").EnumerateArray().ToArray();
        var addresses = leaves.Select(leaf => new DebugVariableAddress(leaf.GetProperty("arrayIdx").GetByte(), leaf.GetProperty("elemIdx").GetUInt16(), leaf.GetProperty("size").GetUInt16())).ToArray();
        var realCounts = await real.GetVariableCountsAsync(); Check(addresses.All(a => a.ArrayIndex < realCounts.Count && a.ElementIndex < realCounts[a.ArrayIndex]), "Real symbol layout");
        var first = await real.ReadVariablesAsync(addresses); await Task.Delay(100); var second = await real.ReadVariablesAsync(addresses);
        var before = DebugValueDecoder.Decode("DINT", first.Values[0].Bytes.Span, identity.LittleEndian);
        var after = DebugValueDecoder.Decode("DINT", second.Values[0].Bytes.Span, identity.LittleEndian);
        Check(int.Parse(after!) > int.Parse(before!), "Real periodic counter increases");
        Console.WriteLine($"PASS real authenticated debug MD5/layout/readonly counter {before} -> {after}, ticks {first.Tick} -> {second.Tick}");
    }
    var socket = new ScriptedSocket();
    await using var client = new DebugSocketClient(new Uri("https://localhost:8443"), transport: socket);
    await client.ConnectAsync("test-token");
    var writesBefore = socket.Commands.Count;
    await client.WriteBooleanAsync("0123456789abcdef0123456789abcdef", new(0, 258, 1), true);
    Check(socket.Commands[writesBefore] == "46 00", "write capability must be probed before modifying variable");
    var boolPdu = socket.Commands[writesBefore + 1].Split(' ').Select(x => Convert.ToByte(x, 16)).ToArray();
    Check(boolPdu.Length == 38 && boolPdu[0] == 0x46 && boolPdu[1] == 1 && boolPdu[34] == 0 && boolPdu[35] == 1 && boolPdu[36] == 2 && boolPdu[37] == 1, "single BOOL packet differs from bounded protocol");
    Check(Encoding.ASCII.GetString(boolPdu, 2, 32) == "0123456789abcdef0123456789abcdef", "write must bind exact program identity");
    Check(socket.Commands.Skip(writesBefore).All(c => !c.StartsWith("42")), "single write must never use force/unforce");
    await Expect<ArgumentException>(() => client.WriteBooleanAsync("bad", new(0, 0, 1), true));
    await Expect<ArgumentException>(() => client.WriteBooleanAsync("0123456789abcdef0123456789abcdef", new(0, 0, 4), true));
    Console.WriteLine("PASS identity-bound BOOL single-write packet and no force fallback");
    var readStart = socket.Commands.Count;
    Check(client.IsConnected && socket.Sent.Contains("40/api/debug,{\"token\":\"test-token\"}"), "Exact namespace auth handshake");
    var md5 = await client.GetProgramMd5Async();
    Check(md5.Md5 == "0123456789abcdef0123456789abcdef" && md5.LittleEndian, "Source verified MD5 and endian sentinel");
    var counts = await client.GetVariableCountsAsync(); Check(counts.SequenceEqual(new ushort[] { 2, 3 }), "FC41 array metadata");
    var values = await client.ReadVariablesAsync([new(0, 1, 4), new(1, 2, 1)]);
    Check(values.Tick == 25 && values.Values.Count == 2 && values.Values[0].Bytes.Span.SequenceEqual(new byte[] { 123, 0, 0, 0 }) && values.Values[1].Bytes.Span[0] == 1, "FC44 exact metadata based split");
    Check(socket.Sent.Contains("3"), "EngineIO ping serviced");
    Check(socket.Commands.Contains("44 00 02 00 00 01 01 00 02"), "Array + BE element addressing");
    Check(socket.Commands.Skip(readStart).All(c => c.StartsWith("41") || c.StartsWith("44") || c.StartsWith("45")), "Only read PDUs after explicit single-write test");
    Console.WriteLine("PASS exact auth namespace, MD5, counts, FC44 addressing, native values, ping, read whitelist");
    await using (var rejected = new DebugSocketClient(new Uri("https://localhost"), transport: new ScriptedSocket { RejectAuth = true }))
        await Expect<UnauthorizedAccessException>(() => rejected.ConnectAsync("token"));
    Console.WriteLine("PASS namespace authentication rejection");
    await using (var invalidAck = new DebugSocketClient(new Uri("https://localhost"), transport: new ScriptedSocket { AuthReplyOverride = "40/api/debug,{}" }))
        await Expect<InvalidDataException>(() => invalidAck.ConnectAsync("token"));
    Console.WriteLine("PASS namespace acknowledgement requires session identity");
    var silentSocket = new ScriptedSocket { Silent = true };
    await using (var silent = new DebugSocketClient(new Uri("https://localhost"), timeout: TimeSpan.FromMilliseconds(100), transport: silentSocket)) {
        await silent.ConnectAsync("token");
        await Expect<TimeoutException>(() => silent.GetProgramMd5Async());
        Check(!silent.IsConnected && silentSocket.State == WebSocketState.Aborted, "Timeout must abort uncorrelated transport");
    }
    Console.WriteLine("PASS bounded timeout invalidates connection");
    await using (var canceled = new DebugSocketClient(new Uri("https://localhost"), transport: new ScriptedSocket { Silent = true })) {
        await canceled.ConnectAsync("token"); using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Expect<OperationCanceledException>(() => canceled.GetProgramMd5Async(cancellation.Token));
        Check(!canceled.IsConnected, "Canceled outstanding request must abort");
    }
    Console.WriteLine("PASS cancellation invalidates connection");
    foreach (var invalidReply in new[] { "42/api/debug,[\"debug_response\",{\"success\":true,\"data\":\"45 83\"}]", "42/api/debug,[\"debug_response\",{\"success\":true,\"data\":\"GG\"}]", "42/api/debug," + new string('x', 20000) }) {
        await using var malformed = new DebugSocketClient(new Uri("https://localhost"), transport: new ScriptedSocket { ReplyOverride = invalidReply });
        await malformed.ConnectAsync("token"); await Expect<InvalidDataException>(() => malformed.GetProgramMd5Async());
        Check(!malformed.IsConnected, "Malformed reply must invalidate transport");
    }
    Console.WriteLine("PASS short PDU, malformed hex, bounded fragmented frames");
    await Expect<ArgumentException>(() => client.ReadVariablesAsync([new(0, 0, 4096)]));
    await Expect<ArgumentException>(() => client.ReadVariablesAsync([]));
    var incompleteSocket = new ScriptedSocket();
    await using (var incomplete = new DebugSocketClient(new Uri("https://localhost"), transport: incompleteSocket)) {
        await incomplete.ConnectAsync("token");
        await Expect<InvalidDataException>(() => incomplete.ReadVariablesAsync([new(0, 1, 4)]));
    }
    Console.WriteLine("PASS request limits and response metadata mismatch");
    foreach (var mode in new[] { "unsupported", "identity", "timeout" })
    {
        var writeSocket = new ScriptedSocket { SupportsWrite = mode != "unsupported", WriteStatus = mode == "identity" ? (byte)0x84 : (byte)0x7e, SilentWrite = mode == "timeout" };
        await using var writer = new DebugSocketClient(new Uri("https://localhost"), timeout:TimeSpan.FromMilliseconds(150), transport:writeSocket);
        await writer.ConnectAsync("token");
        if (mode == "timeout") await Expect<TimeoutException>(() => writer.WriteBooleanAsync("0123456789abcdef0123456789abcdef", new(0, 1, 1), false));
        else await Expect<InvalidDataException>(() => writer.WriteBooleanAsync("0123456789abcdef0123456789abcdef", new(0, 1, 1), false));
        Check(!writer.IsConnected, "write rejection/unknown must invalidate binding");
        Check(writeSocket.Commands.Count(c => c.StartsWith("46 01")) == (mode == "unsupported" ? 0 : 1), "write retried or unsupported runtime was modified");
        Check(writeSocket.Commands.All(c => !c.StartsWith("42")), "write failure used force fallback");
    }
    Console.WriteLine("PASS unsupported write, changed identity and unknown timeout never force or retry");
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
return 0;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Expect<T>(Func<Task> action) where T : Exception {
    try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
}

sealed class ScriptedSocket : WebSocket
{
    private readonly Channel<string> received = Channel.CreateUnbounded<string>();
    private WebSocketState state = WebSocketState.Open;
    public List<string> Sent { get; } = [];
    public List<string> Commands { get; } = [];
    public bool RejectAuth { get; init; }
    public bool Silent { get; init; }
    public bool SupportsWrite { get; init; } = true;
    public byte WriteStatus { get; init; } = 0x7e;
    public bool SilentWrite { get; init; }
    public string? ReplyOverride { get; init; }
    public string? AuthReplyOverride { get; init; }
    private byte[]? current;
    private int offset;
    public ScriptedSocket() { received.Writer.TryWrite("0{\"sid\":\"fixture\",\"upgrades\":[],\"pingInterval\":25000,\"pingTimeout\":60000,\"maxPayload\":1000000}"); }
    public override WebSocketState State => state;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override void Abort() { state = WebSocketState.Aborted; received.Writer.TryComplete(); }
    public override void Dispose() => Abort();
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { Abort(); return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (current is null) { current = Encoding.UTF8.GetBytes(await received.Reader.ReadAsync(cancellationToken)); offset = 0; }
        var count = Math.Min(Math.Min(buffer.Count, 7), current.Length - offset);
        current.AsSpan(offset, count).CopyTo(buffer.AsSpan()); offset += count;
        var end = offset == current.Length; if (end) current = null;
        return new(count, WebSocketMessageType.Text, end);
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(buffer); Sent.Add(text);
        if (text.StartsWith("40/api/debug,")) {
            if (RejectAuth) { received.Writer.TryWrite("44/api/debug,{\"message\":\"Connection rejected by server\"}"); return Task.CompletedTask; }
            received.Writer.TryWrite("42/api/debug,[\"connected\",{\"status\":\"ok\"}]");
            received.Writer.TryWrite("2"); received.Writer.TryWrite(AuthReplyOverride ?? "40/api/debug,{\"sid\":\"namespace-id\"}");
        }
        if (text.StartsWith("42/api/debug,")) {
            using var json = JsonDocument.Parse(text[13..]); var command = json.RootElement[1].GetProperty("command").GetString()!; Commands.Add(command);
            if (Silent) return Task.CompletedTask;
            if (SilentWrite && command.StartsWith("46 01")) return Task.CompletedTask;
            if (ReplyOverride is not null) { received.Writer.TryWrite(ReplyOverride); return Task.CompletedTask; }
            var bytes = command == "46 00" ? (SupportsWrite ? new byte[] { 0x46, 0x7e, 1 } : new byte[] { 0x46, 0x81 })
                : command.StartsWith("46 01") ? new byte[] { 0x46, WriteStatus }
                : command.StartsWith("45") ? new byte[] { 0x45, 0x7e }.Concat(Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef")).Concat(new byte[] { 0xad, 0xde }).ToArray()
                : command.StartsWith("41") ? [0x41, 2, 0x7e, 0, 2, 0, 3]
                : new byte[] { 0x44, 0x7e, 0, 1, 0, 0, 0, 25, 0, 5, 123, 0, 0, 0, 1 };
            received.Writer.TryWrite("42/api/debug," + JsonSerializer.Serialize(new object[] { "debug_response", new { success = true, data = string.Join(" ", bytes.Select(b => b.ToString("X2"))) } }));
        }
        return Task.CompletedTask;
    }
}


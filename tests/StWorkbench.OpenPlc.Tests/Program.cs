using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StWorkbench.OpenPlc;

var tests = new (string Name, Func<Task> Run)[] {
    ("optional owned SSH tunnel real HTTPS reads and debug authentication", async () => {
        if (Environment.GetEnvironmentVariable("ST_REAL_TUNNEL_VERIFY") != "1") { Console.WriteLine("SKIP owned tunnel real verification (not configured)"); return; }
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var knownHosts = Path.Combine(workspace, "artifacts", "ssh-known-hosts");
        const string pin = "246b1274cbc242f060c0ba098fda3aac05ac873b561f765967a4e81e22caa9c4";
        using var tunnel = await SshRuntimeTunnel.ConnectAsync("192.168.1.31", "a", knownHosts, pin);
        var token = await new SshSessionBroker().AuthenticateAsync("192.168.1.31", "a", knownHosts);
        using var client = new OpenPlcClient(tunnel.Endpoint, certificateFingerprint: pin); client.UseSessionToken(token);
        if (!(await client.GetVersionAsync()).ToString().Contains("4.3.0")) throw new Exception("Unexpected Runtime version");
        if (!(await client.GetStatusAsync()).ToString().Contains("STOPPED")) throw new Exception("Unexpected Runtime state");
        await client.GetLogsAsync(); await client.GetCapabilitiesAsync();
        await using var debug = new DebugSocketClient(tunnel.Endpoint, pin);
        await debug.ConnectAsync(token);
        Equal(true, debug.IsConnected);
    }),
    ("private tunnel rejects unsafe target, missing pin and host identity before launching SSH", async () => {
        await ThrowsAsync<ArgumentException>(() => SshRuntimeTunnel.ConnectAsync("-device", "a", "missing", new string('A',64)));
        await ThrowsAsync<ArgumentException>(() => SshRuntimeTunnel.ConnectAsync("device", "a;cmd", "missing", new string('A',64)));
        await ThrowsAsync<ArgumentException>(() => SshRuntimeTunnel.ConnectAsync("device", "a", "missing", ""));
        await ThrowsAsync<FileNotFoundException>(() => SshRuntimeTunnel.ConnectAsync("device", "a", "missing", new string('A',64)));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => SshRuntimeTunnel.ConnectAsync("device", "a", typeof(SshRuntimeTunnel).Assembly.Location, new string('A',64), canceled.Token));
    }),
    ("debug scalar decoder respects size, sign and target endianness", () => {
        Equal("-2", DebugValueDecoder.Decode("DINT", new byte[] { 0xff, 0xff, 0xff, 0xfe }, false));
        Equal("-2", DebugValueDecoder.Decode("DINT", new byte[] { 0xfe, 0xff, 0xff, 0xff }, true));
        Equal("TRUE", DebugValueDecoder.Decode("BOOL", new byte[] {1}, true));
        Equal(null, DebugValueDecoder.Decode("DINT", new byte[] {0}, true));
        Equal(null, DebugValueDecoder.Decode("STRUCT", new byte[] {0}, true));
        return Task.CompletedTask;
    }),
    ("optional real private Runtime SSH session and pinned HTTPS reads", async () => {
        if (Environment.GetEnvironmentVariable("ST_REAL_RUNTIME_VERIFY") != "1") { Console.WriteLine("SKIP real device verification (not configured)"); return; }
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var token = await new SshSessionBroker().AuthenticateAsync("192.168.1.31", "a", Path.Combine(workspace, "artifacts", "ssh-known-hosts"));
        using var client = new OpenPlcClient(new Uri("https://localhost:18443"), certificateFingerprint: "246b1274cbc242f060c0ba098fda3aac05ac873b561f765967a4e81e22caa9c4");
        client.UseSessionToken(token); Equal(true, client.IsAuthenticated);
        var version = await client.GetVersionAsync();
        if (!version.ToString().Contains("4.3.0")) throw new Exception("Unexpected real Runtime version");
        var status = await client.GetStatusAsync();
        if (status.GetProperty("status").GetString() != "STATUS:STOPPED") throw new Exception("Real Runtime is not confirmed stopped");
        await client.GetCapabilitiesAsync(); await client.GetLogsAsync();
    }),
    ("SSH session token is memory only and invalid token clears authentication", () => {
        using var client = new OpenPlcClient(new Uri("https://localhost:8443"));
        client.UseSessionToken("abc.def_xyz"); Equal(true, client.IsAuthenticated);
        Throws<ArgumentException>(() => client.UseSessionToken("invalid\nsecret")); Equal(false, client.IsAuthenticated);
        return Task.CompletedTask;
    }),
    ("device certificate pin permits only exact identity without hostname errors", () => {
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN=localhost",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
        var pin=Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
        var colonPin=string.Join(':',Enumerable.Range(0,32).Select(i=>pin.Substring(i*2,2)));
        Equal(true,DeviceCertificateTrust.Validate(cert,SslPolicyErrors.None,pin));
        Equal(true,DeviceCertificateTrust.Validate(cert,SslPolicyErrors.RemoteCertificateChainErrors,colonPin));
        Equal(false,DeviceCertificateTrust.Validate(cert,SslPolicyErrors.RemoteCertificateNameMismatch,pin));
        Equal(false,DeviceCertificateTrust.Validate(cert,SslPolicyErrors.RemoteCertificateNameMismatch|SslPolicyErrors.RemoteCertificateChainErrors,pin));
        Equal(false,DeviceCertificateTrust.Validate(cert,SslPolicyErrors.RemoteCertificateNotAvailable,pin));
        Equal(false,DeviceCertificateTrust.Validate(cert,SslPolicyErrors.None,new string('0',64)));
        Equal(false,DeviceCertificateTrust.Validate(cert,SslPolicyErrors.None,"invalid"));
        Equal(false,DeviceCertificateTrust.Validate(null,SslPolicyErrors.None,pin));
        return Task.CompletedTask;
    }),
    ("HTTPS and clean origin required", () => {
        foreach (var uri in new[] { "http://device/", "https://u:p@device/", "https://device/?x=1", "https://device/#x", "https://device/path" })
            Throws<ArgumentException>(() => new OpenPlcClient(new Uri(uri)));
        return Task.CompletedTask;
    }),
    ("JWT login and read endpoints", async () => {
        var seen = new List<string>();
        var handler = new Handler(async request => {
            seen.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath == "/api/login") {
                Equal(HttpMethod.Post, request.Method);
                Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
                Equal("admin", body.RootElement.GetProperty("username").GetString());
                Equal("secret", body.RootElement.GetProperty("password").GetString());
                Equal(null, request.Headers.Authorization);
                return Response("{\"access_token\":\"abc.def.xyz\"}");
            }
            Equal(HttpMethod.Get, request.Method);
            Equal("Bearer abc.def.xyz", request.Headers.Authorization!.ToString());
            return Response("{\"status\":\"STOPPED\"}");
        });
        using var http = new HttpClient(handler);
        using var client = new OpenPlcClient(new Uri("https://device:8443/"), http);
        await client.LoginAsync("admin", "secret");
        Equal(true, client.IsAuthenticated);
        Equal("STOPPED", (await client.GetStatusAsync()).GetProperty("status").GetString());
        await client.GetCompilationStatusAsync(); await client.GetLogsAsync(12,"WARNING");
        Equal("/api/login,/api/status,/api/compilation-status,/api/runtime-logs?id=12&level=WARNING", string.Join(',',seen));
        Equal(null, http.DefaultRequestHeaders.Authorization);
        client.ResetAuthentication();
        Equal(false, client.IsAuthenticated);
        await ThrowsAsync<InvalidOperationException>(() => client.GetStatusAsync());
    }),
    ("failed relogin clears old token and redacts body", async () => {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(++count == 1 ? Response("{\"access_token\":\"old\"}") : Response("password secret token old", HttpStatusCode.Unauthorized))));
        using var client = new OpenPlcClient(new Uri("https://device/"),http);
        await client.LoginAsync("admin","secret");
        var exception = await ThrowsAsync<Exception>(() => client.LoginAsync("admin","secret"));
        Equal(false,client.IsAuthenticated); Equal(false,exception.ToString().Contains("secret"));
        Equal(false,exception.ToString().Contains("token old")); Equal(2,count);
    }),
    ("401 clears token and never retries", async () => {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(++count == 1 ? Response("{\"access_token\":\"old\"}") : Response("sensitive",HttpStatusCode.Unauthorized))));
        using var client = new OpenPlcClient(new Uri("https://device/"),http);
        await client.LoginAsync("admin","secret");
        await ThrowsAsync<Exception>(() => client.GetStatusAsync()); Equal(2,count); Equal(false,client.IsAuthenticated);
    }),
    ("unsafe device writes refused without any request", async () => {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => { count++; return Task.FromResult(Response("{}")); }));
        using var client = new OpenPlcClient(new Uri("https://device/"),http);
        await ThrowsAsync<NotSupportedException>(() => client.UploadAsync(new MemoryStream()));
        await ThrowsAsync<NotSupportedException>(() => client.StartAsync());
        await ThrowsAsync<NotSupportedException>(() => client.StopAsync()); Equal(0,count);
    }),
    ("monitor binding requires exact nonempty identity", () => {
        var expected = new MonitorIdentity("source","symbols","session");
        Throws<InvalidOperationException>(() => new ReadOnlyMonitorSession(expected,expected with { RuntimeSession="other" },TimeSpan.FromSeconds(1)));
        Throws<ArgumentException>(() => new ReadOnlyMonitorSession(expected with { SourceHash="" },expected,TimeSpan.FromSeconds(1)));
        var monitor = new ReadOnlyMonitorSession(expected,expected,TimeSpan.FromSeconds(1));
        Throws<InvalidOperationException>(() => monitor.Observe(expected with { SymbolHash="other" },new("Main.counter","INT","1",1,null,DateTimeOffset.UtcNow,SampleQuality.Good)));
        return Task.CompletedTask;
    }),
    ("monitor expires and disconnects with null value", () => {
        var id = new MonitorIdentity("source","symbols","session");
        var now = DateTimeOffset.UtcNow;
        var monitor = new ReadOnlyMonitorSession(id,id,TimeSpan.FromSeconds(1));
        monitor.Observe(id,new("Main.counter","INT","42",1,now.AddMilliseconds(-20),now,SampleQuality.Good));
        Equal("42",monitor.GetSample("Main.counter",now).Value);
        var expired = monitor.GetSample("Main.counter",now.AddSeconds(2));
        Equal(SampleQuality.Expired,expired.Quality); Equal(null,expired.Value);
        Equal(null,monitor.GetSample("Main.absent",now).Value);
        monitor.MarkUnavailable(now.AddSeconds(3));
        Equal(SampleQuality.Unavailable,monitor.GetSample("Main.counter",now.AddSeconds(3)).Quality);
        Equal(null,monitor.GetSample("Main.counter",now.AddSeconds(3)).Value);
        return Task.CompletedTask;
    }),
    ("trend bounded gaps and device timestamps retained", () => {
        var now = DateTimeOffset.UtcNow;
        var buffer = new TrendBuffer(3);
        Throws<ArgumentOutOfRangeException>(() => new TrendBuffer(0));
        buffer.Add(new("x","INT","1",1,now.AddSeconds(-1),now,SampleQuality.Good));
        buffer.Add(new("x","INT","4",4,now.AddSeconds(-1),now,SampleQuality.Good));
        Equal(3,buffer.Points.Count); Equal(true,buffer.Points[1].IsGap);
        Equal(now.AddSeconds(-1),buffer.Points[2].Sample!.DeviceSampleTime);
        buffer.Add(new("x","INT",null,5,null,now,SampleQuality.Unavailable));
        Equal(3,buffer.Points.Count); Equal(true,buffer.Points[2].IsGap);
        Throws<InvalidOperationException>(() => buffer.Add(new("x","INT","2",2,null,now,SampleQuality.Good)));
        return Task.CompletedTask;
    }),
    ("timeout covers response body and clears auth", async () => {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowContent() }))) { Timeout=TimeSpan.FromMilliseconds(25) };
        using var client = new OpenPlcClient(new Uri("https://device/"),http);
        await ThrowsAsync<OpenPlcException>(() => client.LoginAsync("admin","secret"));
        Equal(false,client.IsAuthenticated);
    }),
    ("version capabilities are unauthenticated and server failure is explicit", async () => {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request => {
            Equal(null,request.Headers.Authorization); paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(paths.Count == 3 ? Response("secret",HttpStatusCode.ServiceUnavailable) : Response("{\"version\":\"unverified\"}"));
        }));
        using var client = new OpenPlcClient(new Uri("https://device/"),http);
        await client.GetVersionAsync(); await client.GetCapabilitiesAsync();
        var ex = await ThrowsAsync<OpenPlcException>(() => client.GetVersionAsync());
        Equal(HttpStatusCode.ServiceUnavailable,ex.StatusCode); Equal(false,ex.ToString().Contains("secret"));
        Equal("/api/version,/api/capabilities,/api/version",string.Join(',',paths));
    }),
    ("reset during pending login prevents late authentication", async () => {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async _ => { started.SetResult(); await release.Task; return Response("{\"access_token\":\"old\"}"); }));
        using var client = new OpenPlcClient(new Uri("https://device/"),http);
        var login = client.LoginAsync("admin","secret"); await started.Task;
        client.ResetAuthentication(); release.SetResult(); await login;
        Equal(false,client.IsAuthenticated);
    }),
    ("oversized response bodies are rejected with and without declared length", async () => {
        foreach (var unknownLength in new[] { false,true }) {
            using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = unknownLength ? new LargeContent() : new StringContent("{\"status\":\"" + new string('x',3*1024*1024) + "\"}") })));
            using var client = new OpenPlcClient(new Uri("https://device/"),http);
            var ex = await ThrowsAsync<OpenPlcException>(() => client.GetVersionAsync());
            Equal(true,ex.Message.Contains("limit"));
        }
    }),
    ("invalid bearer token rejected before header construction", async () => {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Response("{\"access_token\":\"secret\\u0000\"}"))));
        using var client = new OpenPlcClient(new Uri("https://device/"),http);
        var ex = await ThrowsAsync<OpenPlcException>(() => client.LoginAsync("admin","secret"));
        Equal(false,ex.ToString().Contains("secret")); Equal(false,client.IsAuthenticated);
    }),
    ("malformed and transport failures do not leak secrets", async () => {
        foreach (var mode in new[] {0,1,2,3}) {
            using var http = new HttpClient(new Handler(_ => mode == 0 ? Task.FromResult(Response("{secret")) : mode == 1 ? throw new HttpRequestException("secret") : mode == 2 ? throw new TaskCanceledException("secret") : throw new IOException("secret")));
            using var client = new OpenPlcClient(new Uri("https://device/"),http);
            var ex = await ThrowsAsync<OpenPlcException>(() => client.LoginAsync("admin","secret"));
            Equal(false,ex.ToString().Contains("secret")); Equal(false,client.IsAuthenticated);
        }
    })
};
var failed = 0;
foreach (var (name, run) in tests) { try { await run(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
return failed == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T:Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static async Task<T> ThrowsAsync<T>(Func<Task> action) where T:Exception { try { await action(); } catch (T ex) { return ex; } throw new Exception("Expected " + typeof(T).Name); }
static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body,Encoding.UTF8,"application/json") };
sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) => send(request);
}
sealed class SlowContent:HttpContent {
    protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context) => Write(stream,CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context,CancellationToken cancellationToken) => Write(stream,cancellationToken);
    private static async Task Write(Stream stream,CancellationToken token) { await Task.Delay(250,token); await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"access_token\":\"late\"}"),token); }
    protected override bool TryComputeLength(out long length) { length=0; return false; }
}
sealed class LargeContent:HttpContent {
    protected override async Task SerializeToStreamAsync(Stream stream,TransportContext? context) {
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\""));
        await stream.WriteAsync(Encoding.UTF8.GetBytes(new string('x',3*1024*1024)));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("\"}"));
    }
    protected override bool TryComputeLength(out long length) { length=0; return false; }
}

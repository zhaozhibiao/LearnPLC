using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StWorkbench.Toolchain;

/// <summary>Persistent, paired TLS transport. Mutations are sent once; uncertain replies never retry.</summary>
public sealed class EngineeringClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string pairingToken;
    private readonly CancellationTokenSource lifetime = new();
    public Uri Origin { get; }
    public EngineeringClient(Uri origin, string pairingToken, string certificateFingerprint = "", HttpClient? httpClient = null)
    {
        if (!origin.IsAbsoluteUri || origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("Engineering service requires an HTTPS origin.");
        if (string.IsNullOrWhiteSpace(pairingToken) || pairingToken.Length > 8192 || pairingToken.Any(c => c < 33 || c > 126)) throw new ArgumentException("Invalid engineering pairing token.");
        Origin = origin; this.pairingToken = pairingToken;
        if (httpClient is not null) http = httpClient;
        else
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            if (!string.IsNullOrWhiteSpace(certificateFingerprint))
            {
                var pin = certificateFingerprint.Replace(":", "", StringComparison.Ordinal);
                if (pin.Length != 64 || !pin.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid device certificate fingerprint.");
                handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) => cert is not null &&
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0 && CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(pin));
            }
            http = new(handler) { Timeout = Timeout.InfiniteTimeSpan }; ownsHttp = true;
        }
    }
    public async Task<string> OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync("/engineering/session", new { }, TimeSpan.FromSeconds(30), cancellationToken);
        var root = reply.RootElement;
        if (!root.TryGetProperty("access_token", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 8192 } token || token.Any(c => c < 33 || c > 126))
            throw new InvalidDataException("Engineering service did not confirm a Runtime session.");
        return token;
    }
    public async Task<DeploymentResponse> ExecuteAsync(DeploymentRequest request, CancellationToken cancellationToken = default)
    {
        if (!DeploymentActions.IsSupported(request.Action)) throw new ArgumentException("Unsupported engineering action.");
        DeploymentResponse Unknown() => new(false, "unknown", request.RequestId, "工程服务未确认操作结果，请查询真实设备状态；不会自动重试。", null, null);
        try
        {
            using var reply = await SendAsync("/engineering/deploy", request, TimeSpan.FromMinutes(6), cancellationToken);
            var response = reply.RootElement.Deserialize<DeploymentResponse>(Json);
            if (response is null || response.RequestId != request.RequestId || string.IsNullOrWhiteSpace(response.State)) return Unknown();
            return response with { Message = response.Message ?? "", Log = response.Log ?? "", AxisMessage = response.AxisMessage ?? "", MotionMessage = response.MotionMessage ?? "", CommunicationMessage = response.CommunicationMessage ?? "", MotionCycleMessage = response.MotionCycleMessage ?? "" };
        }
        catch (EngineeringAuthorizationException) { return new(false,"unauthorized",request.RequestId,"工程服务授权已失效，请重新配对设备。",null,null); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unknown(); }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or InvalidDataException or InvalidOperationException) { return Unknown(); }
    }
    private async Task<JsonDocument> SendAsync(string path, object body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token); deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, path));
        request.Headers.Add("X-Engineering-Token", pairingToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new EngineeringAuthorizationException();
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Engineering service request was not confirmed.");
        const int maximum = 48 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Engineering service response exceeds limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); using var buffer = new MemoryStream(); var chunk = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(chunk, deadline.Token)) != 0)
        {
            if (buffer.Length + count > maximum) throw new InvalidDataException("Engineering service response exceeds limit.");
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
    }
    public void Dispose() { lifetime.Cancel(); if (ownsHttp) http.Dispose(); lifetime.Dispose(); }
}

public sealed class EngineeringAuthorizationException : Exception;

public static class DeploymentActions
{
    public static bool IsSupported(string action) => action is "stage" or "compile" or "status" or "realtime-status" or "adopt-baseline" or "adopt-empty-baseline" or "activate" or "recover" or "start" or "stop" or "hardware-validate" or "hardware-apply" or "configuration-plan" or "configuration-apply" or "hardware-stage" or "program-export" or "hardware-export" or "program-debug-map";
}

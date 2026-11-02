using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
namespace StWorkbench.OpenPlc;

/// <summary>A sanitized Runtime failure. Response bodies, credentials and underlying exception messages are excluded.</summary>
public sealed class OpenPlcException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// OpenPLC v4 read adapter. Normal certificate validation applies and automatic redirects are disabled.
/// Injected clients must retain certificate validation, disable redirects and avoid logging secrets.
/// Responses are detached JSON because the supported Runtime revision has not yet been pinned.
/// No operation retries automatically; tokens are held only in memory. Instances serialize HTTP operations.
/// </summary>
public sealed class OpenPlcClient : IDisposable
{
    /// <summary>Maximum JSON body accepted for one read, including logs: two MiB.</summary>
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly Uri origin;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim requests = new(1,1);
    private readonly object authentication = new();
    private string? token;
    private long authenticationGeneration;
    private bool disposed;
    private readonly string? engineeringToken;

    public OpenPlcClient(Uri baseUri, HttpClient? httpClient = null, string? certificateFingerprint = null, string? engineeringToken = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        this.engineeringToken = engineeringToken;
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(baseUri.Host)
            || baseUri.UserInfo.Length != 0 || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0 || baseUri.AbsolutePath != "/")
            throw new ArgumentException("A HTTPS origin without credentials, path, query or fragment is required.",nameof(baseUri));
        origin = baseUri;
        ownsHttp = httpClient is null;
        var handler = httpClient is null ? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !baseUri.IsLoopback } : null;
        if (handler is not null && !string.IsNullOrEmpty(certificateFingerprint))
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => DeviceCertificateTrust.Validate(certificate, errors, certificateFingerprint);
        http = httpClient ?? new HttpClient(handler!) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool IsAuthenticated { get { lock(authentication) return token is not null; } }
    public void UseSessionToken(string sessionToken)
    {
        ResetAuthentication(); ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(sessionToken) || sessionToken.Length > 12000 || sessionToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~' or '+' or '/' or '='))) throw new ArgumentException("Invalid session token.", nameof(sessionToken));
        lock (authentication) token = sessionToken;
    }

    /// <summary>Clear the prior token before login. Failed or interrupted login leaves the client unauthenticated.</summary>
    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        ResetAuthentication();
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        long generation;
        lock(authentication) generation = authenticationGeneration;
        using var content = JsonContent.Create(new { username,password });
        var response = await SendAsync(HttpMethod.Post,"api/login",content,false,cancellationToken).ConfigureAwait(false);
        if (!response.TryGetProperty("access_token",out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~' or '+' or '/' or '=')))
            throw new OpenPlcException("Runtime login returned an invalid token.");
        lock(authentication) { if (generation == authenticationGeneration) token = value.GetString(); }
    }

    /// <summary>Forget local authentication; does not claim server-side logout or revocation.</summary>
    public void ResetAuthentication() { lock(authentication) { token = null; authenticationGeneration++; } }
    public Task<JsonElement> GetStatusAsync(CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Get,"api/status",null,true,cancellationToken);
    public Task<JsonElement> GetCompilationStatusAsync(CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Get,"api/compilation-status",null,true,cancellationToken);
    public Task<JsonElement> GetLogsAsync(long minimumId = 0, string level = "DEBUG", CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumId);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        return SendAsync(HttpMethod.Get,$"api/runtime-logs?id={minimumId.ToString(System.Globalization.CultureInfo.InvariantCulture)}&level={Uri.EscapeDataString(level)}",null,true,cancellationToken);
    }
    public Task<JsonElement> GetVersionAsync(CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Get,"api/version",null,false,cancellationToken);
    public Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Get,"api/capabilities",null,false,cancellationToken);

    /// <summary>Disabled until a pinned target profile and safe runtime controls are independently verified.</summary>
    public Task<JsonElement> StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Runtime start is disabled until target version and control semantics are verified.");
    /// <summary>Disabled until verified; a normal PLC stop must never be presented as an emergency stop.</summary>
    public Task<JsonElement> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Runtime stop is disabled until target version and control semantics are verified.");
    /// <summary>Always refuses upload: no approved staging/activation/package ABI profile exists in this adapter.</summary>
    public Task UploadAsync(Stream package, CancellationToken cancellationToken = default) => throw new NotSupportedException("Device upload requires independently verified package ABI, isolated staging, activation and recovery capabilities.");

    private async Task<JsonElement> SendAsync(HttpMethod method,string endpoint,HttpContent? content,bool authenticated,CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (http.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(http.Timeout);
            var requestToken=deadline.Token;
            using var request = new HttpRequestMessage(method,new Uri(origin,endpoint)) { Content = content };
            if (engineeringToken is not null) request.Headers.Add("X-Engineering-Token", engineeringToken);
            if (authenticated)
            {
                string? currentToken;
                lock(authentication) currentToken = token;
                if (currentToken is null) throw new InvalidOperationException("Runtime login is required.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",currentToken);
            }
            using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,requestToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) ResetAuthentication();
            if (!response.IsSuccessStatusCode) throw new OpenPlcException($"Runtime request failed with HTTP {(int)response.StatusCode}.",response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new OpenPlcException("Runtime response exceeds the configured size limit.");
            // Read with the same cancellation token so body reads are cancellable as well.
            using var body = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(buffer,requestToken).ConfigureAwait(false)) != 0)
            {
                if (bounded.Length + read > MaximumResponseBytes)
                    throw new OpenPlcException("Runtime response exceeds the configured size limit.");
                bounded.Write(buffer,0,read);
            }
            bounded.Position=0;
            using var json = await JsonDocument.ParseAsync(bounded,cancellationToken: requestToken).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new OpenPlcException("Runtime returned an invalid JSON response.");
            return json.RootElement.Clone();
        }
        catch (JsonException) { throw new OpenPlcException("Runtime returned malformed JSON."); }
        catch (HttpRequestException) { throw new OpenPlcException("Runtime transport failed; device state may be unknown."); }
        catch (IOException) { throw new OpenPlcException("Runtime response transport failed; device state may be unknown."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new OpenPlcException("Runtime request timed out; device state may be unknown."); }
        catch (OperationCanceledException) { throw new OperationCanceledException("Runtime request was cancelled.",cancellationToken); }
        finally { requests.Release(); }
    }

    public void Dispose() { disposed = true; ResetAuthentication(); if (ownsHttp) http.Dispose(); }
}

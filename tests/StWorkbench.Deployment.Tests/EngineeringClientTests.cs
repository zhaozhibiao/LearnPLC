using System.Net;
using System.Text;
using StWorkbench.Toolchain;

internal static class EngineeringClientTests
{
    public static async Task RunAsync()
    {
        var type = typeof(RemoteDeploymentClient).Assembly.GetType("StWorkbench.Toolchain.EngineeringClient") ?? throw new Exception("Engineering HTTPS transport is not implemented");
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            if (request.RequestUri?.Host != "192.168.1.31" || request.RequestUri.Port != 18445 || request.Headers.GetValues("X-Engineering-Token").Single() != "paired-device-token") throw new Exception("service identity/authentication lost");
            if (request.RequestUri.AbsolutePath == "/engineering/session") return Reply("{\"access_token\":\"runtime-session-token\"}");
            var payload = await request.Content!.ReadAsStringAsync();
            if(payload.Contains("package_path")||payload.Contains("package_base64")||payload.Contains("package_sha256"))throw new Exception("null request fields violate engineering API schema");
            if (!payload.Contains("\"action\":\"status\"")) throw new Exception("deployment action lost");
            return Reply("{\"success\":true,\"state\":\"stopped\",\"request_id\":\"service-read1\",\"active\":{\"build_hash\":null}}");
        }));
        using var client = (IDisposable)Activator.CreateInstance(type, new Uri("https://192.168.1.31:18445"), "paired-device-token", "", http)!;
        var session = (Task<string>)type.GetMethod("OpenSessionAsync")!.Invoke(client, [CancellationToken.None])!;
        if (await session != "runtime-session-token") throw new Exception("runtime session not returned");
        var operation = (Task<DeploymentResponse>)type.GetMethod("ExecuteAsync")!.Invoke(client, [new DeploymentRequest("status", "service-read1"), CancellationToken.None])!;
        if (!(await operation).Success || calls != 2) throw new Exception("direct HTTPS state read failed");
        using var uncertainHttp = new HttpClient(new Handler(_ => Task.FromResult(Reply("{\"success\":true,\"state\":\"running\",\"request_id\":\"wrong\"}"))));
        using var uncertain = (IDisposable)Activator.CreateInstance(type, new Uri("https://192.168.1.31:18445"), "paired-device-token", "", uncertainHttp)!;
        var bad = (Task<DeploymentResponse>)type.GetMethod("ExecuteAsync")!.Invoke(uncertain, [new DeploymentRequest("start", "service-control1"), CancellationToken.None])!;
        if ((await bad).Success) throw new Exception("wrong service request identity accepted");
        Console.WriteLine("PASS persistent engineering HTTPS session/state transport and mismatched response refusal without SSH");
    }
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}

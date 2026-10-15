using System.Text.Json;
using StWorkbench.Toolchain;

internal static class ProgramDownloadWorkflowTests
{
    private static readonly string Old = new('a', 64);
    private static readonly string New = new('b', 64);

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        async Task Test(string name, Func<Task> body)
        {
            try { await body(); Console.WriteLine("PASS download workflow " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL download workflow " + name + ": " + error.Message); }
        }

        foreach (var running in new[] { true, false })
            await Test(running ? "running stops then activates" : "stopped activates without stop", async () =>
            {
                var host = Ready(running);
                var result = await ProgramDownloadWorkflow.ExecuteAsync(new(New, "payload", "checksum"), host);
                Equal(ProgramDownloadState.Activated, result.State);
                Equal(running ? "stage,compile,compiled,status,confirm,status,invalidate,stop,activate,status" : "stage,compile,compiled,status,confirm,status,invalidate,activate,status", host.Trace);
                Equal(("payload", "checksum"), host.Payload);
                Equal(new ProgramRestartNotice(running, true, Old), host.Notice);
                Equal("stopped", result.Status!.State);
            });

        await Test("cancel retains compiled candidate and leaves running program alone", async () =>
        {
            var host = Ready(true); host.Accept = false;
            Equal(ProgramDownloadState.Cancelled, (await ProgramDownloadWorkflow.ExecuteAsync(new(New, "payload", "checksum"), host)).State);
            Equal("stage,compile,compiled,status,confirm", host.Trace);
        });
        await Test("blocked compilation applies metadata without confirmation", async () =>
        {
            var host = Ready(true); host.CanApply = false;
            Equal(ProgramDownloadState.Blocked, (await ProgramDownloadWorkflow.ExecuteAsync(new(New, "payload", "checksum"), host)).State);
            Equal("stage,compile,compiled", host.Trace);
        });
        await Test("apply compiled respects gate before status", async () =>
        {
            var host = Ready(false); host.CanApply = false;
            Equal(ProgramDownloadState.Blocked, (await ProgramDownloadWorkflow.ApplyCompiledAsync(New, host)).State);
            Equal("", host.Trace);
        });
        foreach (var failure in new[] { "stage", "compile" })
            await Test(failure + " failure prevents later work", async () =>
            {
                var host = Ready(true); host.Override = action => action == failure ? Response("failed", Old, false) : null;
                await Refuses(() => ProgramDownloadWorkflow.ExecuteAsync(new(New, "p", "s"), host));
                Equal(failure == "stage" ? "stage" : "stage,compile", host.Trace);
            });
        await Test("compile must explicitly report compiled", async () =>
        {
            var host = Ready(true); host.Override = action => action == "compile" ? Response("staged", Old) : null;
            await Refuses(() => ProgramDownloadWorkflow.ExecuteAsync(new(New, "p", "s"), host));
            Equal("stage,compile", host.Trace);
        });
        foreach (var malformed in new JsonElement?[] { null, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { build_hash = "bad" }), JsonSerializer.SerializeToElement(new { build_hash = 4 }), JsonSerializer.SerializeToElement("bad") })
            await Test("unregistered or malformed baseline refused", async () =>
            {
                var host = Ready(false); host.Override = action => action == "status" ? new(true, "stopped", "id", "", malformed, null) : null;
                await Refuses(() => ProgramDownloadWorkflow.ApplyCompiledAsync(New, host));
                Equal("status", host.Trace);
            });
        await Test("registered empty baseline allowed", async () =>
        {
            var host = Ready(false, null);
            Equal(ProgramDownloadState.Activated, (await ProgramDownloadWorkflow.ApplyCompiledAsync(New, host)).State);
            Equal(null, host.Notice!.ActiveBuildHash);
        });
        await Test("same active hash ignores case and preserves running state", async () =>
        {
            var host = Ready(true, New.ToUpperInvariant());
            var result = await ProgramDownloadWorkflow.ApplyCompiledAsync(New, host);
            Equal(ProgramDownloadState.AlreadyActive, result.State);
            Equal("running", result.Status!.State);
            Equal("status", host.Trace);
        });
        foreach (var changedState in new[] { false, true })
            await Test("confirmation baseline " + (changedState ? "state" : "identity") + " change refuses writes", async () =>
            {
                var host = Ready(true); host.OnConfirm = () => host.Override = action => action == "status" ? Response(changedState ? "stopped" : "running", changedState ? Old : New) : null;
                await Refuses(() => ProgramDownloadWorkflow.ApplyCompiledAsync(New, host));
                Equal("status,confirm,status", host.Trace);
            });
        foreach (var boundary in new[] { "stage", "compile", "compiled", "status", "confirm", "invalidate", "stop", "activate" })
            await Test("stale identity after " + boundary + " prevents further actions", async () =>
            {
                var host = Ready(true); host.After = action => { if (action == boundary) host.IsCurrent = false; };
                await Refuses(() => ProgramDownloadWorkflow.ExecuteAsync(new(New, "p", "s"), host));
                Equal(boundary, host.Events[^1]);
            });
        await Test("initial stale identity sends nothing", async () =>
        {
            var host = Ready(true); host.IsCurrent = false;
            await Refuses(() => ProgramDownloadWorkflow.ApplyCompiledAsync(New, host));
            Equal("", host.Trace);
        });
        foreach (var stop in new[] { Response("stopped", Old, false), Response("running", Old), Response("stopped", New) })
            await Test("unverified stop prevents activation", async () =>
            {
                var host = Ready(true); host.Override = action => action == "stop" ? stop : null;
                await Refuses(() => ProgramDownloadWorkflow.ApplyCompiledAsync(New, host));
                Equal("status,confirm,status,invalidate,stop", host.Trace);
            });
        foreach (var activation in new[] { Response("unknown", New, false), Response("activated-not-running", Old), Response("running", New), Response("activated-not-running", New, false) })
            await Test("unverified activation stops without retry", async () =>
            {
                var host = Ready(false); host.Override = action => action == "activate" ? activation : null;
                await Refuses(() => ProgramDownloadWorkflow.ApplyCompiledAsync(New, host));
                Equal("status,confirm,status,invalidate,activate", host.Trace);
            });
        foreach (var final in new[] { Response("unknown", New, false), Response("running", New), Response("stopped", Old), Response("stopped", New, false) })
            await Test("final status must verify stopped candidate", async () =>
            {
                var host = Ready(false); host.Override = action => action == "status" && host.Activated ? final : null;
                await Refuses(() => ProgramDownloadWorkflow.ApplyCompiledAsync(New, host));
                Equal("status,confirm,status,invalidate,activate,status", host.Trace);
            });
        await Test("transport exception propagates without retry", async () =>
        {
            var host = Ready(false); var expected = new IOException("uncertain");
            host.Override = action => action == "activate" ? throw expected : null;
            try { await ProgramDownloadWorkflow.ApplyCompiledAsync(New, host); throw new Exception("exception missing"); }
            catch (IOException error) { Equal(expected, error); }
            Equal("status,confirm,status,invalidate,activate", host.Trace);
        });
        return failures;
    }

    private static FakeHost Ready(bool running, string? baseline = "default") => new(running, baseline == "default" ? Old : baseline);
    private static DeploymentResponse Response(string state, string? hash, bool success = true) => new(success, state, "id", "response " + state, JsonSerializer.SerializeToElement(new { build_hash = hash }), null);
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static async Task Refuses(Func<Task<ProgramDownloadOutcome>> body)
    {
        try { await body(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("unsafe workflow accepted");
    }

    private sealed class FakeHost(bool running, string? baseline) : IProgramDownloadHost
    {
        public bool IsCurrent { get; set; } = true;
        public bool CanApply { get; set; } = true;
        public bool Accept { get; set; } = true;
        public bool Activated { get; private set; }
        public List<string> Events { get; } = [];
        public string Trace => string.Join(",", Events);
        public (string?, string?) Payload { get; private set; }
        public ProgramRestartNotice? Notice { get; private set; }
        public Func<string, DeploymentResponse?>? Override { get; set; }
        public Action? OnConfirm { get; set; }
        public Action<string>? After { get; set; }
        private void Record(string action) { Events.Add(action); After?.Invoke(action); }
        public Task<DeploymentResponse> SendAsync(string action, string? packageBase64 = null, string? packageHash = null)
        {
            Record(action);
            if (action == "stage") Payload = (packageBase64, packageHash);
            var custom = Override?.Invoke(action);
            if (custom != null) return Task.FromResult(custom);
            var result = action switch
            {
                "stage" => Response("staged", baseline),
                "compile" => Response("compiled", baseline),
                "status" => Response(Activated || !running ? "stopped" : "running", Activated ? New : baseline),
                "stop" => Response("stopped", baseline),
                "activate" => Response("activated-not-running", New),
                _ => throw new Exception("Unexpected action " + action)
            };
            if (action == "activate") Activated = true;
            return Task.FromResult(result);
        }
        public void ApplyCompilation(DeploymentResponse result) => Record("compiled");
        public Task<bool> ConfirmRestartAsync(ProgramRestartNotice notice) { Record("confirm"); Notice = notice; OnConfirm?.Invoke(); return Task.FromResult(Accept); }
        public Task BeforeProgramChangeAsync() { Record("invalidate"); return Task.CompletedTask; }
    }
}

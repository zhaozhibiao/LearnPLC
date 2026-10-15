using System.Collections.Concurrent;
using StWorkbench.Core;
using StWorkbench.Toolchain;

namespace StWorkbench.Mcp;

public sealed record BuildView(string OperationId, string SessionId, string Revision, string State, bool Success,
    string? BuildHash, Diagnostic[] Diagnostics, string Log);
public sealed class BuildService(WorkspaceService workspace, ServerOptions options) : IAsyncDisposable
{
    private sealed class Job(string id, string session, string revision)
    {
        public BuildView View = new(id, session, revision, "Queued", false, null, [], "");
        public PackageResult? Package;
        public CancellationTokenSource Cancellation = new(TimeSpan.FromMinutes(5));
        public Task Completion = Task.CompletedTask;
    }
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly SemaphoreSlim buildGate = new(1);
    private bool disposed;
    public async Task<BuildView> StartAsync(string session, string revision)
    {
        var draft = await workspace.ReadAsync(session);
        if (draft.Revision != revision) throw new InvalidOperationException("Draft revision conflict.");
        var snapshot = ProjectValidator.Capture(draft.Project, draft.Sources);
        var names = draft.Project.Items.Where(i => i.Kind == ItemKind.Program).ToDictionary(i => i.Id, i => i.Name);
        lock (jobs)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (jobs.Count >= 32) throw new InvalidOperationException("Build job capacity reached; restart server after retaining required results.");
            var id = Guid.NewGuid().ToString("N");
            var job = new Job(id, session, revision);
            jobs[id] = job;
            job.Completion = RunAsync(job, snapshot, names);
            return Volatile.Read(ref job.View);
        }
    }
    private async Task RunAsync(Job job, BuildSnapshot snapshot, Dictionary<Guid, string> names)
    {
        var entered = false;
        try
        {
            await buildGate.WaitAsync(job.Cancellation.Token); entered = true;
            Volatile.Write(ref job.View, job.View with { State = "Running" });
            var builder = new PackageBuilder(Path.Combine(options.RepositoryRoot, "tools", "runtime-compat"));
            var package = await builder.BuildAsync(snapshot, names, options.NodeExecutable,
                Path.Combine(options.RepositoryRoot, "tools", "compiler-adapter", "compile.mjs"), options.Distribution,
                workspace.Resolve(".st-mcp-builds"), job.Cancellation.Token);
            job.Package = package;
            Volatile.Write(ref job.View, job.View with { State = package.Success ? "Completed" : "Failed", Success = package.Success,
                BuildHash = package.Success ? package.BuildHash : null, Diagnostics = package.Compilation.Diagnostics.Take(256).ToArray(), Log = Tail(package.Compilation.Log) });
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        { Volatile.Write(ref job.View, job.View with { State = job.Cancellation.IsCancellationRequested ? "Cancelled" : "Failed", Log = Tail(e.Message) }); }
        finally { if (entered) buildGate.Release(); }
    }
    public BuildView Read(string id) => Volatile.Read(ref Get(id).View);
    public void Cancel(string id) => Get(id).Cancellation.Cancel();
    public PackageResult Package(string id)
    {
        var job = Get(id);
        if (Volatile.Read(ref job.View) is not { Success: true, State: "Completed" } || job.Package is null) throw new InvalidOperationException("Build did not produce a verified package.");
        return job.Package;
    }
    private Job Get(string id) => jobs.TryGetValue(id, out var job) ? job : throw new ArgumentException("Unknown build operation.");
    private static string Tail(string text) => text.Length > 16384 ? "[earlier log truncated]\n" + text[^16384..] : text;
    public async ValueTask DisposeAsync()
    {
        Job[] pending;
        lock (jobs)
        {
            if (disposed) return;
            disposed = true; pending = jobs.Values.ToArray();
            foreach (var job in pending) job.Cancellation.Cancel();
        }
        // Compiler cancellation tears down its own child process before service shutdown completes.
        await Task.WhenAll(pending.Select(j => j.Completion));
        foreach (var job in pending) job.Cancellation.Dispose();
    }
}

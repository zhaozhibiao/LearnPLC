using System.Collections.Concurrent;
using System.Text.Json;

namespace StWorkbench.Mcp;

public sealed record OperationView(string OperationId, string State, JsonElement? Result = null, string? Error = null);
public sealed class OperationJobs : IAsyncDisposable
{
    private sealed class Job(string id) { public OperationView View = new(id, "Running"); public Task Completion = Task.CompletedTask; }
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly CancellationTokenSource shutdown = new();
    private bool disposed;
    public CancellationToken ShutdownToken => shutdown.Token;
    public OperationView Start(Func<Task<object>> action)
    {
        lock (jobs)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (jobs.Count >= 64) throw new InvalidOperationException("Operation capacity reached (64); retain required results then restart server.");
            var id = Guid.NewGuid().ToString("N"); var job = new Job(id); jobs[id] = job;
            job.Completion = RunAsync(job, action); return job.View;
        }
    }
    private static async Task RunAsync(Job job, Func<Task<object>> action)
    {
        try
        {
            var result = JsonSerializer.SerializeToElement(await action(), McpJson.Options);
            var state = result.TryGetProperty("state", out var value) && value.GetString() is "Unknown" or "Failed" or "Blocked" or "Cancelled" ? value.GetString()! : "Completed";
            Volatile.Write(ref job.View, new(job.View.OperationId, state, result));
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        { Volatile.Write(ref job.View, new(job.View.OperationId, e is IOException or OperationCanceledException ? "Unknown" : "Failed", Error: e.Message.Length <= 2048 ? e.Message : e.Message[..2048])); }
    }
    public OperationView Read(string id) => jobs.TryGetValue(id, out var job) ? Volatile.Read(ref job.View) : throw new ArgumentException("Unknown operation ID.");
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (jobs)
        {
            if (disposed) return;
            disposed = true; shutdown.Cancel(); pending = jobs.Values.Select(j => j.Completion).ToArray();
        }
        // Cancels SSH IO and prevents subsequent workflow steps; remote acceptance may remain unknown.
        await Task.WhenAll(pending);
        shutdown.Dispose();
    }
}

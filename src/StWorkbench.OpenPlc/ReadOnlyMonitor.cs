namespace StWorkbench.OpenPlc;
/// <summary>Exact identities negotiated by an independently verified read-only transport.</summary>
public sealed record MonitorIdentity(string SourceHash, string SymbolHash, string RuntimeSession);
public enum SampleQuality { Good, Unavailable, Expired, UnsupportedType }
public sealed record MonitorSample(string InstancePath, string Type, string? Value, long Sequence,
    DateTimeOffset? DeviceSampleTime, DateTimeOffset ReceivedAt, SampleQuality Quality);
public sealed record TrendPoint(MonitorSample? Sample, long Sequence, DateTimeOffset ReceivedAt, bool IsGap);
/// <summary>Single-variable bounded history. Sequence discontinuities and unavailable data create explicit gaps.</summary>
public sealed class TrendBuffer
{
    private readonly Queue<TrendPoint> points = new();
    private readonly object sync = new();
    private long? previousSequence;
    private string? path;
    public int Capacity { get; }
    public TrendBuffer(int capacity) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity); Capacity=capacity; }
    public IReadOnlyList<TrendPoint> Points { get { lock(sync) return points.ToArray(); } }
    public void Add(MonitorSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentOutOfRangeException.ThrowIfNegative(sample.Sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.InstancePath);
        lock(sync)
        {
            if (path is not null && path != sample.InstancePath) throw new InvalidOperationException("A trend buffer belongs to one instance path.");
            if (previousSequence is not null && sample.Sequence <= previousSequence) throw new InvalidOperationException("Sample sequence must increase.");
            if (previousSequence is not null && sample.Sequence - previousSequence > 1)
                Enqueue(new(null,previousSequence.Value+1,sample.ReceivedAt,true));
            var unavailable = sample.Quality != SampleQuality.Good || sample.Value is null;
            Enqueue(new(unavailable ? sample with { Value=null } : sample,sample.Sequence,sample.ReceivedAt,unavailable));
            previousSequence=sample.Sequence; path=sample.InstancePath;
        }
    }
    internal void AddGap(DateTimeOffset receivedAt)
    {
        lock(sync) Enqueue(new(null,previousSequence ?? 0,receivedAt,true));
    }
    private void Enqueue(TrendPoint point) { points.Enqueue(point); while(points.Count > Capacity) points.Dequeue(); }
}

/// <summary>
/// Read-only display/cache model, never a simulated PLC or a debug protocol implementation.
/// An integration must independently verify source/symbol/session identity before supplying actual device samples.
/// Device and workstation timestamps stay separate; stale and missing values are null.
/// </summary>
public sealed class ReadOnlyMonitorSession
{
    private readonly object sync = new();
    private readonly Dictionary<string,MonitorSample> samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string,TrendBuffer> trends = new(StringComparer.Ordinal);
    private readonly TimeSpan maximumAge;
    private readonly int trendCapacity;
    private readonly int maximumVariables;
    public MonitorIdentity Identity { get; }
    public ReadOnlyMonitorSession(MonitorIdentity expected, MonitorIdentity actual, TimeSpan maximumAge, int trendCapacity = 256, int maximumVariables = 256)
    {
        ValidateIdentity(expected); ValidateIdentity(actual);
        if (expected != actual) throw new InvalidOperationException("Runtime source, symbols or running session do not match.");
        if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trendCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumVariables);
        Identity=expected; this.maximumAge=maximumAge; this.trendCapacity=trendCapacity; this.maximumVariables=maximumVariables;
    }
    public void Observe(MonitorIdentity identity, MonitorSample sample)
    {
        if (Identity != identity) throw new InvalidOperationException("Runtime monitor identity changed. Rebind to the matching build and session.");
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.InstancePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.Type);
        ArgumentOutOfRangeException.ThrowIfNegative(sample.Sequence);
        if (!Enum.IsDefined(sample.Quality)) throw new ArgumentException("Unknown sample quality.",nameof(sample));
        if (sample.Quality != SampleQuality.Good) sample=sample with {Value=null};
        else if (sample.Value is null) sample=sample with {Quality=SampleQuality.Unavailable};
        lock(sync)
        {
            if (!trends.TryGetValue(sample.InstancePath,out var trend))
            {
                if (samples.Count >= maximumVariables) throw new InvalidOperationException("Monitor variable capacity reached.");
                trends.Add(sample.InstancePath,trend=new TrendBuffer(trendCapacity));
            }
            trend.Add(sample);
            samples[sample.InstancePath]=sample;
        }
    }
    public MonitorSample GetSample(string path, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock(sync)
        {
            if (!samples.TryGetValue(path,out var sample)) return new(path,"unknown",null,0,null,now,SampleQuality.Unavailable);
            if (sample.Quality == SampleQuality.Good && (now < sample.ReceivedAt || now - sample.ReceivedAt > maximumAge))
            {
                samples[path]=sample=sample with {Value=null,Quality=SampleQuality.Expired};
                trends[path].AddGap(now);
            }
            return sample;
        }
    }
    /// <summary>Returns a detached trend snapshot; unknown variables have no points.</summary>
    public IReadOnlyList<TrendPoint> GetTrend(string path) { lock(sync) return trends.TryGetValue(path,out var trend) ? trend.Points : []; }
    public void MarkUnavailable(DateTimeOffset now)
    {
        lock(sync)
        {
            foreach(var path in samples.Keys.ToArray())
            {
                samples[path]=samples[path] with {Value=null,Quality=SampleQuality.Unavailable};
                trends[path].AddGap(now);
            }
        }
    }
    private static void ValidateIdentity(MonitorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.SourceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.SymbolHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.RuntimeSession);
    }
}

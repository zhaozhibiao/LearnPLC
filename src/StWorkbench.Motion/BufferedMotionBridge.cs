using System.Collections.Immutable;
using System.Threading.Channels;

namespace StWorkbench.Motion;

public sealed class BufferedMotionBridge(int capacity, TimeSpan maximumAge) : IMotionBridge
{
    public int Capacity { get; } = capacity;
    public TimeSpan MaximumAge { get; } = maximumAge;
    private sealed record PublishedState(MotionSnapshot? Snapshot, ImmutableHashSet<PathReference> Paths, bool Available);
    private PublishedState state = new(null, [], false);
    private readonly Channel<MotionCommand> commands = Channel.CreateBounded<MotionCommand>(
        new BoundedChannelOptions(capacity > 0 && maximumAge > TimeSpan.Zero ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity)))
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    public bool Available => Volatile.Read(ref state).Available;
    public MotionSnapshot? Snapshot => Volatile.Read(ref state).Snapshot;

    // Worker publishes a detached, immutable snapshot. Only a connected adapter sets available=true.
    public void PublishState(MotionSnapshot snapshot, IEnumerable<PathReference> paths, bool available) =>
        Volatile.Write(ref state, new(snapshot, paths.ToImmutableHashSet(), available));
    public bool HasPath(PathReference path) => Volatile.Read(ref state).Paths.Contains(path);

    public bool TrySubmit(MotionCommand command, out MotionError error)
    {
        error = Validate(command, command.IssuedAt);
        if (error != MotionError.None) return false;
        if (commands.Writer.TryWrite(command)) return true;
        error = MotionError.QueueFull;
        return false;
    }

    // true means one item was consumed, including a rejected item. Worker must inspect error,
    // send matching failure feedback for a rejection, and NEVER send a rejected item to hardware.
    public bool TryReadForDispatch(DateTimeOffset now, out MotionCommand? command, out MotionError error)
    {
        error = MotionError.None;
        if (!commands.Reader.TryRead(out command)) return false;
        error = Validate(command, now);
        return true;
    }

    private MotionError Validate(MotionCommand command, DateTimeOffset now)
    {
        var current = Volatile.Read(ref state);
        if (!current.Available) return MotionError.Unavailable;
        if (now >= command.ExpiresAt) return MotionError.FeedbackExpired;
        var snapshot = current.Snapshot;
        if (snapshot is null || snapshot.SampledAt > now || now - snapshot.SampledAt > MaximumAge) return MotionError.SnapshotExpired;
        if (snapshot.AxisMappingVersion != command.AxisMappingVersion) return MotionError.AxisMappingMismatch;
        if (snapshot.Lease is null || snapshot.Lease.Id != command.LeaseId || snapshot.Lease.OwnerSessionId != command.SessionId)
            return MotionError.LeaseConflict;
        if (snapshot.Lease.ExpiresAt <= now) return MotionError.LeaseExpired;
        if (command.Request.Kind == MotionCommandKind.ExecutePath &&
            (command.Request.Path is null || !current.Paths.Contains(command.Request.Path))) return MotionError.UnknownPath;
        return MotionError.None;
    }
}

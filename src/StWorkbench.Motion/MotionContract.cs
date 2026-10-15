namespace StWorkbench.Motion;

public enum MotionCommandKind { MoveAbsolute, Home, Stop, ExecutePath }
public enum MotionError { None, Unavailable, QueueFull, LeaseConflict, LeaseExpired, SnapshotExpired, FeedbackExpired, InvalidRequest, UnknownPath, AxisMappingMismatch, DeviceFailure }
public enum FeedbackState { Accepted, Applied, Completed, Failed }
public readonly record struct AxisReference(Guid Id);
public sealed record PathReference(Guid PathId, string PathVersion);
public sealed record MotionRequest(MotionCommandKind Kind, AxisReference Axis, double Position = 0, double Velocity = 1, PathReference? Path = null);
public sealed record ControlLease(Guid Id, Guid OwnerSessionId, DateTimeOffset ExpiresAt);
public sealed record MotionSnapshot(DateTimeOffset SampledAt, string AxisMappingVersion, ControlLease? Lease);
public sealed record MotionCommand(Guid SessionId, long RequestId, MotionRequest Request, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, Guid LeaseId, string AxisMappingVersion);
public sealed record MotionFeedback(Guid SessionId, long RequestId, FeedbackState State, DateTimeOffset SampledAt, MotionError ErrorID = MotionError.None);
public sealed record MotionReceipt(Guid SessionId, long RequestId, bool Done, MotionError ErrorID);
public interface IMotionBridge
{
    bool Available { get; }
    MotionSnapshot? Snapshot { get; }
    bool HasPath(PathReference path);
    bool TrySubmit(MotionCommand command, out MotionError error);
}

public sealed class MotionFunctionBlock(Guid sessionId, string axisMappingVersion, TimeSpan? timeout = null, TimeSpan? maximumAge = null)
{
    public Guid SessionId { get; } = sessionId;
    public string AxisMappingVersion { get; } = axisMappingVersion;
    public TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(5);
    public TimeSpan MaximumAge { get; } = maximumAge ?? TimeSpan.FromSeconds(1);
    private static long nextRequestId;
    private bool previousExecute;
    private MotionCommand? active;
    private MotionReceipt? receipt;
    public bool Busy => active is not null;
    public bool Done => receipt?.Done == true;
    public bool Error => receipt is { ErrorID: not MotionError.None };
    public MotionError ErrorID => receipt?.ErrorID ?? MotionError.None;
    public long RequestId { get; private set; }

    // Called by one scan thread; transport must implement only immediate, bounded local work.
    public void Scan(bool execute, MotionRequest request, IMotionBridge bridge, DateTimeOffset now, MotionFeedback? feedback = null)
    {
        var rising = execute && !previousExecute;
        previousExecute = execute;
        if (active is { } pending)
        {
            if (now >= pending.ExpiresAt)
                Finish(false, MotionError.FeedbackExpired);
            else if (feedback is { } update && update.SessionId == SessionId && update.RequestId == RequestId)
            {
                if (update.SampledAt < pending.IssuedAt || update.SampledAt > now || now - update.SampledAt > MaximumAge)
                    Finish(false, MotionError.FeedbackExpired);
                else if (update.State == FeedbackState.Completed)
                    Finish(true, MotionError.None);
                else if (update.State == FeedbackState.Failed)
                    Finish(false, update.ErrorID == MotionError.None ? MotionError.DeviceFailure : update.ErrorID);
            }
        }
        if (!rising || active is not null || receipt is not null) return;

        RequestId = Interlocked.Increment(ref nextRequestId);
        // Never wrap into IDs that might have appeared in an older session.
        if (RequestId <= 0) throw new InvalidOperationException("Motion request sequence exhausted.");
        var error = Validate(request, bridge, now);
        if (error != MotionError.None) { Finish(false, error); return; }
        var snapshot = bridge.Snapshot!;
        active = new(SessionId, RequestId, request, now, now + Timeout, snapshot.Lease!.Id, AxisMappingVersion);
        if (!bridge.TrySubmit(active, out error))
            Finish(false, error == MotionError.None ? MotionError.Unavailable : error);
    }

    private MotionError Validate(MotionRequest request, IMotionBridge bridge, DateTimeOffset now)
    {
        if (SessionId == Guid.Empty || request.Axis.Id == Guid.Empty || !Enum.IsDefined(request.Kind) ||
            string.IsNullOrWhiteSpace(AxisMappingVersion) || Timeout <= TimeSpan.Zero || MaximumAge <= TimeSpan.Zero ||
            !double.IsFinite(request.Position) || !double.IsFinite(request.Velocity) || request.Velocity <= 0)
            return MotionError.InvalidRequest;
        if (!bridge.Available) return MotionError.Unavailable;
        var snapshot = bridge.Snapshot;
        if (snapshot is null || snapshot.SampledAt > now || now - snapshot.SampledAt > MaximumAge)
            return MotionError.SnapshotExpired;
        if (snapshot.AxisMappingVersion != AxisMappingVersion) return MotionError.AxisMappingMismatch;
        if (snapshot.Lease is null || snapshot.Lease.OwnerSessionId != SessionId || snapshot.Lease.Id == Guid.Empty)
            return MotionError.LeaseConflict;
        if (snapshot.Lease.ExpiresAt <= now) return MotionError.LeaseExpired;
        if (request.Kind == MotionCommandKind.ExecutePath && (request.Path is null || request.Path.PathId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Path.PathVersion) || !bridge.HasPath(request.Path))) return MotionError.UnknownPath;
        return MotionError.None;
    }

    private void Finish(bool done, MotionError error)
    {
        active = null;
        receipt = new(SessionId, RequestId, done, error);
    }

    public MotionReceipt? ConsumeReceipt()
    {
        var consumed = receipt;
        receipt = null;
        return consumed;
    }

    // Local reset only. It does not cancel motion already accepted by a device.
    public void Reset()
    {
        if (Busy) throw new InvalidOperationException("Cannot reset a pending motion request.");
        receipt = null;
    }
}

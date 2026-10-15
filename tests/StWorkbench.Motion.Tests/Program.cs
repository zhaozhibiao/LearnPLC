using StWorkbench.Motion;

var now = DateTimeOffset.Parse("2026-10-13T00:00:00Z");
var session = Guid.NewGuid();
var axis = new AxisReference(Guid.NewGuid());
var request = new MotionRequest(MotionCommandKind.MoveAbsolute, axis, 20, 5);
var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
FixtureBridge Bridge() => new() { Snapshot = new(now, "mapping-1", new(Guid.NewGuid(), session, now.AddMinutes(1))) };
MotionFunctionBlock Block() => new(session, "mapping-1", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));

Test("Held high never repeats; falling edge rearms after consumption", () => {
    var bridge = Bridge(); var fb = Block();
    fb.Scan(true, request, bridge, now);
    var id = fb.RequestId;
    Check(fb.Busy && bridge.Commands.Count == 1, "Rising edge must submit once and report Busy");
    for (var i = 0; i < 10; i++) fb.Scan(true, request, bridge, now);
    Check(bridge.Commands.Count == 1, "Held Execute must not repeat");
    fb.Scan(true, request, bridge, now, new(session, id, FeedbackState.Completed, now));
    Check(fb.Done && !fb.Busy, "Completion must set Done");
    fb.Scan(false, request, bridge, now);
    fb.Scan(true, request, bridge, now);
    Check(fb.Done && bridge.Commands.Count == 1, "Terminal result must latch until consumed");
    Check(fb.ConsumeReceipt()?.RequestId == id && !fb.Done, "Consumption clears receipt");
    fb.Scan(true, request, bridge, now);
    Check(bridge.Commands.Count == 1, "Consumption does not create an Execute edge");
    fb.Scan(false, request, bridge, now); fb.Scan(true, request, bridge, now);
    Check(fb.RequestId > id && bridge.Commands.Count == 2, "Rearmed request needs larger ID");
});
Test("Only matching session and request can complete", () => {
    var bridge = Bridge(); var fb = Block(); fb.Scan(true, request, bridge, now);
    fb.Scan(true, request, bridge, now, new(Guid.NewGuid(), fb.RequestId, FeedbackState.Completed, now));
    fb.Scan(true, request, bridge, now, new(session, fb.RequestId + 1, FeedbackState.Completed, now));
    Check(fb.Busy && !fb.Done, "Mismatched receipt must be rejected");
    fb.Scan(true, request, bridge, now, new(session, fb.RequestId, FeedbackState.Applied, now));
    Check(fb.Busy, "Applied is not motion completion");
});
Test("Expired matching feedback becomes explicit latched error", () => {
    var bridge = Bridge(); var fb = Block(); fb.Scan(true, request, bridge, now);
    fb.Scan(true, request, bridge, now.AddSeconds(2), new(session, fb.RequestId, FeedbackState.Completed, now));
    Check(fb.ErrorID == MotionError.FeedbackExpired && fb.Error && !fb.Done, "Stale feedback cannot succeed");
    fb.Scan(true, request, bridge, now.AddSeconds(2), new(session, fb.RequestId, FeedbackState.Completed, now.AddSeconds(2)));
    Check(fb.Error, "Late feedback must not overwrite terminal receipt");
});
Test("Missing receipt times out without resubmission", () => {
    var bridge = Bridge(); var fb = Block(); fb.Scan(true, request, bridge, now); fb.Scan(true, request, bridge, now.AddSeconds(5));
    Check(fb.ErrorID == MotionError.FeedbackExpired && bridge.Commands.Count == 1, "Timeout must fail once");
});
Test("Lease owner, lease expiry, snapshot freshness and mapping are enforced", () => {
    foreach (var error in new[] { MotionError.LeaseConflict, MotionError.LeaseExpired, MotionError.SnapshotExpired, MotionError.AxisMappingMismatch }) {
        var bridge = Bridge(); var snapshot = bridge.Snapshot!;
        bridge.Snapshot = error switch {
            MotionError.LeaseConflict => snapshot with { Lease = snapshot.Lease! with { OwnerSessionId = Guid.NewGuid() } },
            MotionError.LeaseExpired => snapshot with { Lease = snapshot.Lease! with { ExpiresAt = now } },
            MotionError.SnapshotExpired => snapshot with { SampledAt = now.AddSeconds(-2) },
            _ => snapshot with { AxisMappingVersion = "mapping-2" }
        };
        var fb = Block(); fb.Scan(true, request, bridge, now);
        Check(fb.ErrorID == error && bridge.Commands.Count == 0, "Must reject " + error);
    }
});
Test("Queue full and unavailable never pretend successful motion", () => {
    foreach (var error in new[] { MotionError.QueueFull, MotionError.Unavailable }) {
        var bridge = Bridge(); bridge.SubmitError = error; bridge.Available = error != MotionError.Unavailable;
        var fb = Block(); fb.Scan(true, request, bridge, now); fb.Scan(true, request, bridge, now);
        Check(fb.ErrorID == error && !fb.Done && !fb.Busy, "Must expose " + error);
    }
});
Test("Path command keeps requested immutable ID/version and rejects unknown version", () => {
    var bridge = Bridge(); var path = new PathReference(Guid.NewGuid(), "sha256:v1"); bridge.Path = path;
    var fb = Block(); fb.Scan(true, request with { Kind = MotionCommandKind.ExecutePath, Path = path }, bridge, now);
    Check(bridge.Commands.Single().Request.Path == path, "Path must not be replaced");
    var other = Block(); other.Scan(true, request with { Kind = MotionCommandKind.ExecutePath, Path = path with { PathVersion = "latest" } }, bridge, now);
    Check(other.ErrorID == MotionError.UnknownPath, "Unknown path version must reject");
});
Test("Real bounded local queue refuses overflow and never synthesizes feedback", () => {
    var bridge = new BufferedMotionBridge(1, TimeSpan.FromSeconds(1));
    var fb = Block(); fb.Scan(true, request, bridge, now);
    Check(fb.ErrorID == MotionError.Unavailable, "No worker means unavailable");
    bridge.PublishState(Bridge().Snapshot!, [], true);
    var first = Block(); first.Scan(true, request, bridge, now);
    var second = Block(); second.Scan(true, request, bridge, now);
    Check(first.Busy && second.ErrorID == MotionError.QueueFull, "Capacity must be enforced");
    Check(bridge.TryReadForDispatch(now, out var command, out var error) && error == MotionError.None && command!.RequestId == first.RequestId, "Worker reads original command");
    Check(first.Busy && !first.Done, "Dequeuing is not motion completion");
    Check(!bridge.TryReadForDispatch(now, out _, out _), "Queue must be empty");
});
Test("Worker rechecks renewed lease identity, ownership and expiry before dispatch", () => {
    foreach (var expected in new[] { MotionError.LeaseConflict, MotionError.LeaseExpired, MotionError.FeedbackExpired }) {
        var bridge = new BufferedMotionBridge(1, TimeSpan.FromSeconds(1)); var snapshot = Bridge().Snapshot!;
        bridge.PublishState(snapshot, [], true); var fb = Block(); fb.Scan(true, request, bridge, now);
        var dispatchTime = expected == MotionError.FeedbackExpired ? now.AddSeconds(5) : now;
        bridge.PublishState(snapshot with { SampledAt = dispatchTime, Lease = expected switch {
            MotionError.LeaseConflict => snapshot.Lease! with { Id = Guid.NewGuid() },
            MotionError.LeaseExpired => snapshot.Lease! with { ExpiresAt = now },
            _ => snapshot.Lease
        } }, [], true);
        Check(bridge.TryReadForDispatch(dispatchTime, out _, out var error) && error == expected, "Dispatch must reject " + expected);
    }
});
Test("Device failure latches exact error and explicit reset retains edge state", () => {
    var bridge = Bridge(); var fb = Block(); fb.Scan(true, request, bridge, now);
    try { fb.Reset(); throw new Exception("Busy reset must throw"); } catch (InvalidOperationException) { }
    fb.Scan(true, request, bridge, now, new(session, fb.RequestId, FeedbackState.Failed, now, MotionError.LeaseConflict));
    Check(fb.ErrorID == MotionError.LeaseConflict && !fb.Busy, "Preserve reported failure");
    fb.Reset(); fb.Scan(true, request, bridge, now);
    Check(!fb.Error && bridge.Commands.Count == 1, "Reset cannot synthesize an edge");
});
Test("Home and Stop dispatch distinct kinds with globally increasing request IDs", () => {
    var bridge = Bridge(); var home = Block(); var stop = Block();
    home.Scan(true, request with { Kind = MotionCommandKind.Home }, bridge, now);
    stop.Scan(true, request with { Kind = MotionCommandKind.Stop }, bridge, now);
    Check(stop.RequestId > home.RequestId && bridge.Commands[0].Request.Kind == MotionCommandKind.Home && bridge.Commands[1].Request.Kind == MotionCommandKind.Stop, "Kinds and IDs must remain distinct");
});
Test("Published path catalog is detached from mutable worker input", () => {
    var bridge = new BufferedMotionBridge(1, TimeSpan.FromSeconds(1)); var path = new PathReference(Guid.NewGuid(), "v1");
    var paths = new[] { path }; bridge.PublishState(Bridge().Snapshot!, paths, true);
    paths[0] = path with { PathVersion = "v2" };
    Check(bridge.HasPath(path) && !bridge.HasPath(paths[0]), "Installed catalog must be immutable");
});
Test("Invalid numeric parameters and future feedback cannot succeed", () => {
    var bridge = Bridge(); var invalid = Block(); invalid.Scan(true, request with { Position = double.NaN }, bridge, now);
    Check(invalid.ErrorID == MotionError.InvalidRequest && bridge.Commands.Count == 0, "Reject nonfinite position");
    var fb = Block(); fb.Scan(true, request, bridge, now);
    fb.Scan(true, request, bridge, now, new(session, fb.RequestId, FeedbackState.Completed, now.AddSeconds(1)));
    Check(fb.ErrorID == MotionError.FeedbackExpired && !fb.Done, "Future timestamp cannot complete");
});
ContinuousPathTests.Run();
Console.WriteLine($"{passed} motion contract tests passed.");

sealed class FixtureBridge : IMotionBridge
{
    public bool Available { get; set; } = true;
    public MotionSnapshot? Snapshot { get; set; }
    public PathReference? Path { get; set; }
    public MotionError SubmitError { get; set; }
    public List<MotionCommand> Commands { get; } = [];
    public bool HasPath(PathReference path) => Path == path;
    public bool TrySubmit(MotionCommand command, out MotionError error) {
        error = SubmitError; if (error != MotionError.None) return false; Commands.Add(command); return true;
    }
}

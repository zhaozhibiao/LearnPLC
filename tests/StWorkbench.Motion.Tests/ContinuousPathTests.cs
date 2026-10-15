using StWorkbench.Core;
using StWorkbench.Motion;

static class ContinuousPathTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Refuses(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected safe refusal"); }
    static readonly AxisConfiguration[] Axes = [new("X", "axis0", "mm", -100, 100), new("Y", "axis1", "mm", -100, 100)];
    static MotionPathAsset Asset(params PathSegment[] segments) => new MotionPathAsset { Axes = ["X", "Y"], Segments = segments, Profile = new(CycleSeconds: .01) }.Seal();
    public static void Run()
    {
        var line = Asset(new("Line", new(0, 0), new(10, 0)), new("Line", new(10, 0), new(20, 0)));
        var diagonal = Asset(new PathSegment("Line", new(0, 0), new(100, 100)));
        var diagonalPlan = ContinuousPathPlanner.Prepare(diagonal, Axes);
        if (diagonalPlan.Samples.Any(s => Math.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y) > diagonal.Profile.Velocity * 1.000001)) throw new Exception("Diagonal vector feed exceeds machining profile");
        var prepared = ContinuousPathPlanner.Prepare(line, Axes);
        Check(prepared.Samples[0].Position == new PathPoint(0, 0) && prepared.Samples[^1].Position == new PathPoint(20, 0), "Exact endpoints");
        Check(prepared.Samples[0].Velocity == default && prepared.Samples[^1].Velocity == default, "Rest endpoints");
        Check(prepared.Samples.Where(s => s.Position.X is > 9 and < 11).All(s => s.Velocity.X > .01), "Tangent middle cannot stop");
        Check(prepared.Samples.All(s => s.Position.Y == 0), "Straight geometry");
        var capped = Axes.Select(a => a with { MaximumVelocity = .5, MaximumAcceleration = 1, MaximumDeceleration = .75, MaximumJerk = 2 }).ToArray();
        var limited = ContinuousPathPlanner.Prepare(line, capped);
        VerifyBounds(limited, new(.5, 1, .75, 2));
        Check(limited.DurationSeconds > prepared.DurationSeconds, "Axis caps must slow preparation");
        Refuses(() => ContinuousPathPlanner.Prepare(line, [Axes[0] with { MaximumJerk = double.NaN }, Axes[1]]));
        Refuses(() => ContinuousPathPlanner.Prepare(line, [Axes[0], Axes[1] with { ControllerAxis = Axes[0].ControllerAxis }]));
        var exactLine = (line with { Profile = line.Profile with { CornerTolerance = 0 } }).Seal();
        Check(ContinuousPathPlanner.Prepare(exactLine, Axes).Samples.All(s => s.Position.Y == 0), "Zero tolerance straight line stays exact");
        var corner = Asset(new("Line", new(0, 0), new(5, 0)), new("Line", new(5, 0), new(5, 5)));
        var curved = ContinuousPathPlanner.Prepare(corner, Axes);
        VerifyBounds(curved, corner.Profile);
        Check(curved.Samples.All(s => Math.Min(Math.Abs(s.Position.Y), Math.Abs(s.Position.X - 5)) <= corner.Profile.CornerTolerance), "Corner blend tolerance");
        Refuses(() => ContinuousPathPlanner.Prepare((corner with { Profile = corner.Profile with { CornerTolerance = 0 } }).Seal(), Axes));
        Refuses(() => ContinuousPathPlanner.Prepare(line with { Offset = new(1, 0) }, Axes));
        Refuses(() => ContinuousPathPlanner.Prepare((line with { Offset = new(99, 0) }).Seal(), Axes));
        Refuses(() => ContinuousPathPlanner.Prepare(Asset(new("Line", new(0, 0), new(1, 0)), new("Line", new(2, 0), new(3, 0))), Axes));
        Refuses(() => ContinuousPathPlanner.Prepare((line with { Segments = Enumerable.Repeat(line.Segments[0], 4097).ToArray() }).Seal(), Axes));
        var circle = Asset(new PathSegment("Arc", new(2, 0), new(2, 0), new(0, 0), 2 * Math.PI));
        var round = ContinuousPathPlanner.Prepare(circle, Axes);
        Check(round.Samples.All(s => Math.Abs(Math.Sqrt(s.Position.X * s.Position.X + s.Position.Y * s.Position.Y) - 2) <= circle.Profile.CornerTolerance), "Circle tolerance");
        VerifyBounds(round, circle.Profile);
        var xyz = Asset(new PathSegment("Line", new(0, 0, 1), new(2, 0, 1)));
        Refuses(() => ContinuousPathPlanner.Prepare(xyz, Axes));
        var xyzMapped = (xyz with { Axes = ["X", "Y", "Z"], Offset = new(1, 2, 3) }).Seal();
        var xyzResult = ContinuousPathPlanner.Prepare(xyzMapped, [.. Axes, new("Z", "axis2", "mm", -100, 100)]);
        Check(xyzResult.Samples[0].Position == new PathPoint(1, 2, 4) && xyzResult.Samples[^1].Position == new PathPoint(3, 2, 4), "XYZ offset endpoints");
        Refuses(() => ContinuousPathPlanner.Prepare(Asset(new PathSegment("Line", new(0, 0), new(0, 0))), Axes));
        Refuses(() => ContinuousPathPlanner.Prepare(Asset(new PathSegment("Arc", new(2, 0), new(0, 1), default, Math.PI / 2)), Axes));
        Refuses(() => ContinuousPathPlanner.Prepare((circle with { Profile = circle.Profile with { CornerTolerance = 1e-8 } }).Seal(), Axes));
        var dxf = "0\nSECTION\n2\nENTITIES\n0\nARC\n8\ncut\n10\n1\n20\n2\n30\n3\n40\n2\n50\n0\n51\n90\n0\nENDSEC\n0\nEOF\n";
        var imported = DxfPathImporter.Parse(dxf, 2).Single();
        Check(imported.Layer == "cut" && imported.Segments[0].Kind == "Arc" && imported.Segments[0].Center == new PathPoint(2, 4, 6), "DXF arc XYZ and scaling");
        Check(Math.Abs(imported.Segments[0].SweepRadians - Math.PI / 2) < 1e-12, "DXF sweep");
        Refuses(() => DxfPathImporter.Parse(dxf.Replace("0\nENDSEC", "0\nSPLINE\n0\nENDSEC")));
        Refuses(() => DxfPathImporter.Parse(dxf.Replace("40\n2", "210\n1\n40\n2")));
        Refuses(() => DxfPathImporter.Parse(dxf.Replace("0\nENDSEC\n", "")));
        var lightweight = "0\nSECTION\n2\nENTITIES\n0\nLWPOLYLINE\n90\n3\n70\n1\n38\n4\n10\n0\n20\n0\n10\n2\n20\n0\n10\n2\n20\n2\n0\nENDSEC\n0\nEOF\n";
        var poly = DxfPathImporter.Parse(lightweight).Single();
        Check(poly.Segments.Length == 3 && poly.Segments.All(s => s.Start.Z == 4 && s.End.Z == 4) && poly.Segments[^1].End == poly.Segments[0].Start, "Closed polyline preserves Z and straight geometry");
        Refuses(() => DxfPathImporter.Parse(lightweight.Replace("90\n3", "42\n1\n90\n3")));
        Refuses(() => DxfPathImporter.Parse(lightweight.Replace("70\n1", "70\n1.5")));
        Refuses(() => DxfPathImporter.Parse(new string('x', 8 * 1024 * 1024 + 1)));
        Console.WriteLine("PASS continuous geometry: endpoints, tangents, bounds, corners, arcs, hash, limits, DXF refusal");
    }
    static void VerifyBounds(PreparedTrajectory p, PathProfile limits)
    {
        static double Max(PathPoint v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));
        foreach (var s in p.Samples) { Check(Max(s.Velocity) <= limits.Velocity * 1.000001, "Velocity limit"); Check(Max(s.Acceleration) <= Math.Min(limits.Acceleration, limits.Deceleration) * 1.000001, "Acceleration limit"); }
        for (var i = 1; i < p.Samples.Count; i++) {
            var a = p.Samples[i - 1]; var b = p.Samples[i]; double dt = b.TimeSeconds - a.TimeSeconds;
            Check(dt > 0, "Strict time order");
            Check(Max(new((b.Acceleration.X - a.Acceleration.X) / dt, (b.Acceleration.Y - a.Acceleration.Y) / dt, (b.Acceleration.Z - a.Acceleration.Z) / dt)) <= limits.Jerk * 1.00001, "Sample acceleration derivative jerk");
            Check(Math.Abs((b.Position.X - a.Position.X) / dt - (a.Velocity.X + b.Velocity.X) / 2) <= limits.Acceleration * dt, "Position/velocity consistency");
        }
    }
}

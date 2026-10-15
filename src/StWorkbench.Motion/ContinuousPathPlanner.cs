using StWorkbench.Core;

namespace StWorkbench.Motion;

public sealed record TrajectorySample(double TimeSeconds, PathPoint Position, PathPoint Velocity, PathPoint Acceleration);
public sealed record PreparedTrajectory(IReadOnlyList<TrajectorySample> Samples, double DurationSeconds);

/// <summary>Offline, bounded preparation. C2 piecewise quintics and a global rest-to-rest
/// time law. This is a conservative virtual trajectory, not a certified realtime kernel.</summary>
public static class ContinuousPathPlanner
{
    public const int MaximumGeometryPoints = 50_000;
    public const int MaximumSamples = 250_000;
    public const double MaximumDurationSeconds = 600;
    public static PreparedTrajectory Prepare(MotionPathAsset asset, IReadOnlyList<AxisConfiguration> axes)
    {
        var errors = MotionPathAsset.Validate(asset);
        if (errors.Count != 0) throw new InvalidDataException(string.Join("; ", errors));
        if (axes is null || axes.Any(a => a is null)) throw new InvalidDataException("Invalid axis configuration.");
        // Snapshot the supplied configuration. No realtime allocations or device writes occur here.
        var mapped = asset.Axes.Select(name => {
            var matches = axes.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Unit != "mm" || matches[0].Mechanics is { Kind: not AxisKind.Linear } || !double.IsFinite(matches[0].MinPosition) || !double.IsFinite(matches[0].MaxPosition) || matches[0].MinPosition >= matches[0].MaxPosition) throw new InvalidDataException("Missing/ambiguous linear axis or invalid millimeter limits.");
            return matches[0];
        }).ToArray();
        if (mapped.Any(a => string.IsNullOrWhiteSpace(a.ControllerAxis)) || mapped.Select(a => a.ControllerAxis).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapped.Length) throw new InvalidDataException("Path axes must map to distinct controller axes.");
        double tolerance = asset.Profile.CornerTolerance;
        var nodes = new List<PathPoint>();
        void Append(PathPoint p) {
            p = Add(p, asset.Offset);
            if (nodes.Count > 0 && Length(Sub(p, nodes[^1])) < 1e-12) return;
            if (nodes.Count >= MaximumGeometryPoints) throw new InvalidDataException("Geometry preparation budget exceeded; simplify the contour or increase tolerance.");
            nodes.Add(p);
        }
        foreach (var segment in asset.Segments) {
            Append(segment.Start);
            if (segment.Kind == "Line") {
                var delta = Sub(segment.End, segment.Start); double length = Length(delta);
                if (length < 1e-9) throw new InvalidDataException("Zero-length line.");
                if (tolerance > 0) {
                    double trim = Math.Min(length / 3, tolerance / 4);
                    Append(Add(segment.Start, Mul(delta, trim / length)));
                    Append(Add(segment.End, Mul(delta, -trim / length)));
                }
                Append(segment.End);
            } else {
                if (tolerance == 0) throw new InvalidDataException("Zero-tolerance arcs require an exact analytic executor; this bounded preparer refuses approximation.");
                var start = Sub(segment.Start, segment.Center); double radius = Math.Sqrt(start.X * start.X + start.Y * start.Y);
                double sweep = segment.SweepRadians;
                if (radius < 1e-9 || Math.Abs(sweep) < 1e-12 || Math.Abs(sweep) > 2 * Math.PI + 1e-12 || Math.Abs(start.Z) > 1e-9 || Math.Abs(segment.End.Z - segment.Center.Z) > 1e-9) throw new InvalidDataException("Invalid XY arc geometry.");
                double angle = Math.Atan2(start.Y, start.X);
                var expected = new PathPoint(segment.Center.X + radius * Math.Cos(angle + sweep), segment.Center.Y + radius * Math.Sin(angle + sweep), segment.Center.Z);
                if (Length(Sub(expected, segment.End)) > 1e-7) throw new InvalidDataException("Arc endpoint/sweep mismatch.");
                double stepsRequired = Math.Ceiling(radius * Math.Abs(sweep) / Math.Min(tolerance / 4, radius * .05));
                if (!double.IsFinite(stepsRequired) || stepsRequired > MaximumGeometryPoints - nodes.Count) throw new InvalidDataException("Arc preparation budget exceeded.");
                int steps = Math.Max(2, (int)stepsRequired);
                for (int j = 1; j < steps; j++) {
                    double a = angle + sweep * j / steps;
                    Append(new(segment.Center.X + radius * Math.Cos(a), segment.Center.Y + radius * Math.Sin(a), segment.Center.Z));
                }
                Append(segment.End);
            }
        }
        if (nodes.Count < 2) throw new InvalidDataException("Empty trajectory.");
        var lengths = new double[nodes.Count - 1]; var directions = new PathPoint[lengths.Length];
        for (int i = 0; i < lengths.Length; i++) { var d = Sub(nodes[i + 1], nodes[i]); lengths[i] = Length(d); directions[i] = Mul(d, 1 / lengths[i]); }
        var tangents = new PathPoint[nodes.Count]; tangents[0] = directions[0]; tangents[^1] = directions[^1];
        for (int i = 1; i < tangents.Length - 1; i++) {
            double dot = Dot(directions[i - 1], directions[i]);
            if (dot < -0.999999) throw new InvalidDataException("Reversing cusp requires an explicit stop/entry strategy.");
            if (tolerance == 0 && dot < 1 - 1e-12) throw new InvalidDataException("Zero-tolerance corner cannot be continuously blended.");
            tangents[i] = Mul(Add(directions[i - 1], directions[i]), .5);
        }
        var curves = new List<Curve>(lengths.Length); double total = 0;
        var maxima = new double[3, 3];
        for (int i = 0; i < lengths.Length; i++) {
            double l = lengths[i]; var a = nodes[i]; var b = nodes[i + 1];
            PathPoint[] controls = [a, Add(a, Mul(tangents[i], l / 5)), Add(a, Mul(tangents[i], 2 * l / 5)), Add(b, Mul(tangents[i + 1], -2 * l / 5)), Add(b, Mul(tangents[i + 1], -l / 5)), b];
            foreach (var p in controls) {
                for (int axis = 0; axis < mapped.Length; axis++) if (Coordinate(p, axis) < mapped[axis].MinPosition || Coordinate(p, axis) > mapped[axis].MaxPosition) throw new InvalidDataException("Offset/blend exceeds mapped axis position bounds.");
                if (mapped.Length == 2 && p.Z != 0) throw new InvalidDataException("XYZ geometry requires a Z mapping.");
            }
            var derivative = controls;
            for (int order = 0; order < 3; order++) {
                derivative = Differentiate(derivative, l);
                foreach (var d in derivative) for (int axis = 0; axis < 3; axis++) maxima[order, axis] = Math.Max(maxima[order, axis], Math.Abs(Coordinate(d, axis)));
            }
            var firstDerivative = Differentiate(controls, l);
            curves.Add(new(total, l, controls, firstDerivative, Differentiate(firstDerivative, l))); total += l;
        }
        double duration = asset.Profile.CycleSeconds;
        double VectorBound(int order) => Math.Sqrt(Enumerable.Range(0, mapped.Length).Sum(axis => maxima[order, axis] * maxima[order, axis]));
        double vectorD1 = VectorBound(0), vectorD2 = VectorBound(1), vectorD3 = VectorBound(2);
        duration = Math.Max(duration, 2 * vectorD1 * total / asset.Profile.Velocity);
        duration = Math.Max(duration, Math.Sqrt((4 * vectorD2 * total * total + 6 * vectorD1 * total) / Math.Min(asset.Profile.Acceleration, asset.Profile.Deceleration)));
        duration = Math.Max(duration, Math.Cbrt((8 * vectorD3 * total * total * total + 36 * vectorD2 * total * total + 60 * vectorD1 * total) / asset.Profile.Jerk));
        for (int axis = 0; axis < mapped.Length; axis++) {
            var a = mapped[axis];
            double v = Cap(asset.Profile.Velocity, a.MaximumVelocity);
            double acceleration = Math.Min(Cap(asset.Profile.Acceleration, a.MaximumAcceleration), Cap(asset.Profile.Deceleration, a.MaximumDeceleration));
            double jerk = Cap(asset.Profile.Jerk, a.MaximumJerk);
            // Convex hull derivative bounds and conservative maxima for the quintic time law.
            double d1 = maxima[0, axis], d2 = maxima[1, axis], d3 = maxima[2, axis];
            duration = Math.Max(duration, 2 * d1 * total / v);
            duration = Math.Max(duration, Math.Sqrt((4 * d2 * total * total + 6 * d1 * total) / acceleration));
            duration = Math.Max(duration, Math.Cbrt((8 * d3 * total * total * total + 36 * d2 * total * total + 60 * d1 * total) / jerk));
        }
        duration *= 1.001;
        double sampleIntervals = Math.Ceiling(duration / asset.Profile.CycleSeconds);
        if (!double.IsFinite(duration) || duration > MaximumDurationSeconds || sampleIntervals + 1 > MaximumSamples) throw new InvalidDataException("Prepared time/sample budget exceeded; split or simplify the contour.");
        int intervals = (int)sampleIntervals; duration = intervals * asset.Profile.CycleSeconds;
        var samples = new TrajectorySample[intervals + 1]; int cursor = 0;
        for (int i = 0; i <= intervals; i++) {
            double q = (double)i / intervals, q2 = q * q, q3 = q2 * q;
            double u = q3 * (10 + q * (-15 + 6 * q));
            double du = 30 * q2 * (1 - q) * (1 - q) * total / duration;
            double ddu = (60 * q - 180 * q2 + 120 * q3) * total / (duration * duration);
            double distance = u * total;
            while (cursor < curves.Count - 1 && distance > curves[cursor].Start + curves[cursor].Length) cursor++;
            var curve = curves[cursor]; double t = Math.Clamp((distance - curve.Start) / curve.Length, 0, 1);
            var d1 = Evaluate(curve.FirstDerivative, t);
            var d2 = Evaluate(curve.SecondDerivative, t);
            samples[i] = new(i * asset.Profile.CycleSeconds, Evaluate(curve.Controls, t), Mul(d1, du), Add(Mul(d2, du * du), Mul(d1, ddu)));
        }
        samples[0] = new(0, nodes[0], default, default); samples[^1] = new(duration, nodes[^1], default, default);
        return new(Array.AsReadOnly(samples), duration);
    }
    static double Cap(double requested, double? cap) {
        if (cap is null) return requested;
        if (!double.IsFinite(cap.Value) || cap <= 0) throw new InvalidDataException("Invalid per-axis dynamic cap.");
        return Math.Min(requested, cap.Value);
    }
    sealed record Curve(double Start, double Length, PathPoint[] Controls, PathPoint[] FirstDerivative, PathPoint[] SecondDerivative);
    static PathPoint[] Differentiate(PathPoint[] c, double length) {
        var d = new PathPoint[c.Length - 1]; for (int i = 0; i < d.Length; i++) d[i] = Mul(Sub(c[i + 1], c[i]), d.Length / length); return d;
    }
    static PathPoint Evaluate(PathPoint[] c, double t) {
        Span<PathPoint> work = stackalloc PathPoint[6]; c.CopyTo(work);
        for (int n = c.Length - 1; n > 0; n--) for (int j = 0; j < n; j++) work[j] = Add(Mul(work[j], 1 - t), Mul(work[j + 1], t));
        return work[0];
    }
    static double Coordinate(PathPoint p, int i) => i == 0 ? p.X : i == 1 ? p.Y : p.Z;
    static PathPoint Add(PathPoint a, PathPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    static PathPoint Sub(PathPoint a, PathPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    static PathPoint Mul(PathPoint a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    static double Dot(PathPoint a, PathPoint b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static double Length(PathPoint p) => Math.Sqrt(Dot(p, p));
}

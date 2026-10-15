using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using StWorkbench.Core;
using PathSegment = StWorkbench.Core.PathSegment;

namespace StWorkbench.App;

public sealed class PathPreview : Control
{
    public IReadOnlyList<PathPoint> Points { get; private set; } = [];
    public IReadOnlyList<PathPoint> Trajectory { get; private set; } = [];
    public void SetPath(IEnumerable<PathSegment> segments, PathPoint offset, IEnumerable<PathPoint>? trajectory = null)
    {
        var points = new List<PathPoint>();
        PathPoint Shift(PathPoint p) => new(p.X + offset.X, p.Y + offset.Y, p.Z + offset.Z);
        var bounded = segments.Take(4096).ToArray();
        int perSegmentLimit = Math.Clamp(60_000 / Math.Max(1, bounded.Length), 2, 256);
        foreach (var segment in bounded)
        {
            points.Add(Shift(segment.Start));
            if (segment.Kind == "Arc")
            {
                var radius = Math.Sqrt(Math.Pow(segment.Start.X - segment.Center.X, 2) + Math.Pow(segment.Start.Y - segment.Center.Y, 2));
                var angle = Math.Atan2(segment.Start.Y - segment.Center.Y, segment.Start.X - segment.Center.X);
                int count = Math.Clamp((int)Math.Ceiling(Math.Abs(segment.SweepRadians) * 24), 2, perSegmentLimit);
                for (int i = 1; i < count; i++)
                {
                    var a = angle + segment.SweepRadians * i / count;
                    points.Add(Shift(new(segment.Center.X + radius * Math.Cos(a), segment.Center.Y + radius * Math.Sin(a), segment.Start.Z + (segment.End.Z - segment.Start.Z) * i / count)));
                }
            }
            points.Add(Shift(segment.End));
        }
        Points = points.AsReadOnly();
        var samples = trajectory?.ToArray() ?? [];
        int stride = Math.Max(1, (int)Math.Ceiling(samples.Length / 10_000d));
        Trajectory = samples.Where((_, i) => i % stride == 0 || i == samples.Length - 1).ToArray(); InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brush.Parse("#0D1826"), new Rect(Bounds.Size));
        if (Points.Count == 0 || Bounds.Width <= 40 || Bounds.Height <= 40) return;
        var all = Points.Concat(Trajectory).ToArray();
        double minX = all.Min(p => p.X), minY = all.Min(p => p.Y), dx = Math.Max(1e-6, all.Max(p => p.X) - minX), dy = Math.Max(1e-6, all.Max(p => p.Y) - minY);
        double scale = Math.Min((Bounds.Width - 40) / dx, (Bounds.Height - 40) / dy);
        Point Screen(PathPoint p) => new(20 + (p.X - minX) * scale, Bounds.Height - 20 - (p.Y - minY) * scale);
        void Draw(IReadOnlyList<PathPoint> ps, string color, double width)
        {
            var pen = new Pen(Brush.Parse(color), width);
            for (int i = 1; i < ps.Count; i++) context.DrawLine(pen, Screen(ps[i - 1]), Screen(ps[i]));
        }
        Draw(Points, "#729CC4", 2); Draw(Trajectory, "#79D7BE", 1);
        context.DrawEllipse(Brush.Parse("#FFCE75"), null, Screen(Points[0]), 4, 4);
    }
}

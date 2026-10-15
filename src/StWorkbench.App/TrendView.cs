using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using StWorkbench.OpenPlc;
using System.Globalization;
namespace StWorkbench.App;
public sealed class TrendView : Control
{
    public IReadOnlyList<TrendPoint> Points { get; set; } = [];
    public string Caption { get; set; } = "选择一个数值变量查看趋势";
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Brush.Parse("#101B2B"), new Rect(Bounds.Size));
        var label = new FormattedText(Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, Brushes.LightGray); context.DrawText(label, new Point(10, 8));
        if (Points.Count < 2) return;
        var numbers = Points.Select(p => !p.IsGap && double.TryParse(p.Sample?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? (double?)value : null).ToArray();
        var values = numbers.Where(n => n.HasValue).Select(n => n!.Value).ToArray(); if (values.Length == 0) return;
        var low = values.Min(); var high = values.Max(); if (low == high) { low -= 1; high += 1; }
        var pen = new Pen(Brush.Parse("#75D9D5"), 1.5); Point? previous = null;
        for (var i = 0; i < numbers.Length; i++)
        {
            if (numbers[i] is not { } value) { previous = null; continue; }
            var point = new Point(12 + i * Math.Max(1, Bounds.Width - 24) / (numbers.Length - 1), 35 + (high - value) / (high - low) * Math.Max(1, Bounds.Height - 50));
            if (previous is { } prior) context.DrawLine(pen, prior, point); previous = point;
        }
    }
}

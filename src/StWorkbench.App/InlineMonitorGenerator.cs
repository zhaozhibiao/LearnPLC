using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;
using StWorkbench.OpenPlc;

namespace StWorkbench.App;

public sealed record InlineMonitorValue(int Offset, string Path, string Text, SampleQuality Quality);

// Visual elements occupy zero document characters: copying, saving and compilation see only ST.
public sealed class InlineMonitorGenerator : VisualLineElementGenerator
{
    public IReadOnlyList<InlineMonitorValue> Values { get; set; } = [];
    public override int GetFirstInterestedOffset(int startOffset) => Values.FirstOrDefault(v => v.Offset >= startOffset && v.Offset <= CurrentContext.VisualLine.LastDocumentLine.EndOffset)?.Offset ?? -1;
    public override VisualLineElement ConstructElement(int offset)
    {
        var value = Values.First(v => v.Offset == offset);
        return new ValueElement(value);
    }
    private sealed class ValueElement(InlineMonitorValue value) : FormattedTextElement(" ⟦" + value.Text + "⟧", 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            TextRunProperties.SetForegroundBrush(Brush.Parse(value.Quality != SampleQuality.Good ? "#8996AA" : value.Text == "TRUE" ? "#6EE7A0" : value.Text == "FALSE" ? "#EBC37D" : "#67D5E8"));
            return base.CreateTextRun(startVisualColumn, context);
        }
    }
}

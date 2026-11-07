using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Zyber.Services;

namespace Zyber.Languages;

public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Selection;
    private TextDocument? document;
    private readonly List<(SimpleSegment Segment, int Severity)> segments = [];
    public void SetDiagnostics(TextDocument? currentDocument, IReadOnlyList<LspDiagnostic> diagnostics)
    {
        document = currentDocument;
        segments.Clear();
        if (document is null) return;
        int Offset(LspPosition position)
        {
            if (position.Line < 0 || position.Line >= document.LineCount) throw new IOException("Invalid diagnostic line.");
            var line = document.GetLineByNumber(position.Line + 1);
            if (position.Character < 0 || position.Character > line.Length) throw new IOException("Invalid diagnostic column.");
            return line.Offset + position.Character;
        }
        foreach (var diagnostic in diagnostics)
        {
            try
            {
                var start = Offset(diagnostic.Range.Start);
                var end = Offset(diagnostic.Range.End);
                if (end >= start) segments.Add((new SimpleSegment(start, Math.Min(Math.Max(1, end - start), document.TextLength - start)), diagnostic.Severity));
            }
            catch (IOException) { }
        }
    }
    public ZedTheme? Theme { get; set; }
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document != document || document is null || !textView.VisualLinesValid || textView.VisualLines.Count == 0 || Theme is null) return;
        var visibleStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        var visibleEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var (segment, severity) in segments)
        {
            if (segment.EndOffset < visibleStart || segment.Offset > visibleEnd) continue;
            var pen = new Pen(Theme.Brush(severity == 1 ? "error" : severity == 2 ? "warning" : "info"), 1);
            foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                var right = Math.Max(rectangle.Right, rectangle.Left + 5);
                for (var x = rectangle.Left; x < right; x += 4)
                {
                    drawingContext.DrawLine(pen, new Point(x, rectangle.Bottom - 1), new Point(Math.Min(x + 2, right), rectangle.Bottom - 3));
                    drawingContext.DrawLine(pen, new Point(Math.Min(x + 2, right), rectangle.Bottom - 3), new Point(Math.Min(x + 4, right), rectangle.Bottom - 1));
                }
            }
        }
    }
}

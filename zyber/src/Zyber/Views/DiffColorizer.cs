using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Zyber.Services;

namespace Zyber.Views;

public sealed class DiffColorizer(Func<bool> enabled, Func<ZedTheme?> theme) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        var colors = theme();
        if (!enabled() || colors is null || line.Length == 0) return;
        var text = CurrentContext.Document.GetText(line);
        var token = text.StartsWith('+') ? "created" : text.StartsWith('-') ? "deleted" : text.StartsWith("@@", StringComparison.Ordinal) ? "text.accent" : null;
        if (token is null) return;
        ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(colors.Color(token))));
    }
}

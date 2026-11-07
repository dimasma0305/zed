using AvaloniaEdit.Document;

namespace Zyber.Models;

public sealed class DiffTab(string title, string text) : WorkspaceTab
{
    public override string Name { get; } = title;
    public override string Caption => Name;
    public TextDocument Document { get; } = new(text);
}

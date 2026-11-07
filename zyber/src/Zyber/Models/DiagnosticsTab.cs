using System.Collections.ObjectModel;
using Zyber.Languages;

namespace Zyber.Models;

public sealed record DiagnosticItem(string Path, LspDiagnostic Diagnostic)
{
    public string Location => $"{System.IO.Path.GetFileName(Path)}:{Diagnostic.Range.Start.Line + 1}:{Diagnostic.Range.Start.Character + 1}";
    public string Message => Diagnostic.Message;
    public string Severity => Diagnostic.Severity switch { 1 => "Error", 2 => "Warning", 3 => "Information", _ => "Hint" };
}
public sealed class DiagnosticsTab : WorkspaceTab
{
    public override string Name => "Project Diagnostics";
    public override string Caption => "Project Diagnostics";
    public ObservableCollection<DiagnosticItem> Items { get; } = [];
}

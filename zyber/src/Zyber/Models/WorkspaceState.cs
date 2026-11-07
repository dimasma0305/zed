using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using AvaloniaEdit.Document;
using Zyber.Services;

namespace Zyber.Models;

public abstract class ObservableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

public sealed class EditorTab : WorkspaceTab
{
    public string? FilePath { get; private set; }
    public override string Name => FilePath is null ? "untitled" : Path.GetFileName(FilePath);
    public override string Caption => Name + (IsDirty ? "  •" : "") + (HasExternalChanges ? "  !" : "");
    private bool externalChanges;
    public bool HasExternalChanges { get => externalChanges; set { externalChanges = value; Notify(); Notify(nameof(Caption)); } }
    public string? Fingerprint { get; private set; }
    public Encoding Encoding { get; private set; } = new UTF8Encoding(false, true);
    public TextDocument Document { get; }
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public override bool IsDirty => !Document.UndoStack.IsOriginalFile;

    public EditorTab(TextFileSnapshot? snapshot = null)
    {
        FilePath = snapshot?.Path;
        Fingerprint = snapshot?.Fingerprint;
        Encoding = snapshot?.Encoding ?? Encoding;
        Document = new TextDocument(snapshot?.Text ?? "");
        Document.UndoStack.MarkAsOriginalFile();
        Document.UndoStack.PropertyChanged += (_, _) => { Notify(nameof(IsDirty)); Notify(nameof(Caption)); };
    }

    public void RecordSave(string path, string fingerprint, string savedText)
    {
        FilePath = path;
        Fingerprint = fingerprint;
        HasExternalChanges = false;
        // A save can finish after the user has typed more; those edits must remain dirty.
        if (Document.Text == savedText) Document.UndoStack.MarkAsOriginalFile();
        Notify(nameof(FilePath)); Notify(nameof(Name)); Notify(nameof(Caption)); Notify(nameof(IsDirty));
    }
    public bool ReloadIfUnchanged(TextFileSnapshot snapshot, string? expectedFingerprint)
    {
        if (IsDirty || Fingerprint != expectedFingerprint || !ProjectFiles.PathComparer.Equals(FilePath, snapshot.Path)) return false;
        Document.Text = snapshot.Text;
        Document.UndoStack.ClearAll();
        Document.UndoStack.MarkAsOriginalFile();
        Fingerprint = snapshot.Fingerprint;
        Encoding = snapshot.Encoding;
        CaretOffset = Math.Min(CaretOffset, Document.TextLength);
        HasExternalChanges = false;
        return true;
    }

}

public sealed class FileNode : ObservableState
{
    public string Name { get; }
    public string? FullPath { get; }
    public bool IsDirectory { get; }
    public string Icon => IsDirectory ? (IsExpanded ? "folder_open" : "folder") : (Path.GetExtension(FullPath) ?? "").ToLowerInvariant() switch
    {
        ".json" or ".jsonc" => "json", ".md" => "file_markdown", ".rs" => "file_rust", ".toml" => "file_toml",
        ".cs" or ".py" or ".js" or ".ts" or ".cpp" or ".go" => "code", _ => "file_text_outlined"
    };
    public ObservableCollection<FileNode> Children { get; } = [];
    private bool expanded;
    private bool loaded;
    public bool IsExpanded
    {
        get => expanded;
        set { expanded = value; Notify(); Notify(nameof(Icon)); if (value && !loaded) LoadChildren(); }
    }

    public FileNode(string path, bool directory)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
        IsDirectory = directory;
        if (directory) Children.Add(new FileNode("Loading…"));
    }

    private FileNode(string message) { Name = message; }

    private async void LoadChildren()
    {
        if (FullPath is null) return;
        loaded = true;
        try
        {
            var entries = await Task.Run(() => ReadDirectory(FullPath));
            Children.Clear();
            foreach (var entry in entries) Children.Add(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Children.Clear();
            Children.Add(new FileNode("Cannot read folder: " + exception.Message));
        }
    }

    public static async Task RefreshAsync(ObservableCollection<FileNode> nodes, string path, Func<bool>? isCurrent = null)
    {
        var entries = await Task.Run(() => ReadDirectory(path));
        if (isCurrent is not null && !isCurrent()) return;
        var existing = nodes.Where(node => node.FullPath is not null).ToDictionary(node => node.FullPath!, ProjectFiles.PathComparer);
        var next = entries.Select(entry => existing.TryGetValue(entry.FullPath!, out var old) && old.IsDirectory == entry.IsDirectory ? old : entry).ToList();
        if (!nodes.SequenceEqual(next))
        {
            nodes.Clear();
            foreach (var entry in next) nodes.Add(entry);
        }
        foreach (var node in nodes.Where(node => node.IsDirectory && node.IsExpanded).ToArray())
            await RefreshAsync(node.Children, node.FullPath!, isCurrent);
    }

    public static List<FileNode> ReadDirectory(string path) => new DirectoryInfo(path)
        .EnumerateFileSystemInfos()
        .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0 && entry.Name != ".git")
        .OrderByDescending(entry => (entry.Attributes & FileAttributes.Directory) != 0)
        .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .Select(entry => new FileNode(entry.FullName, (entry.Attributes & FileAttributes.Directory) != 0))
        .ToList();
}

public sealed class WorkspaceState : ObservableState
{
    public ObservableCollection<WorkspaceTab> Tabs { get; } = [];
    public ObservableCollection<FileNode> Files { get; } = [];
    private WorkspaceTab? activeTab;
    public WorkspaceTab? ActiveTab
    {
        get => activeTab;
        set { activeTab = value; currentSymbol = ""; Notify(); Notify(nameof(HasDocument)); Notify(nameof(ShowWelcome)); Notify(nameof(Breadcrumb)); Notify(nameof(ActiveDocument)); Notify(nameof(ActiveSearch)); Notify(nameof(HasSearch)); Notify(nameof(HasDiagnostics)); Notify(nameof(ActiveDiagnostics)); Notify(nameof(HasEditor)); Notify(nameof(ActiveDiff)); }
    }
    private string? folder;
    public string? Folder
    {
        get => folder;
        set { folder = value; Notify(); Notify(nameof(ProjectName)); Notify(nameof(Breadcrumb)); Notify(nameof(HasProject)); Notify(nameof(NoProject)); Notify(nameof(ProjectHeader)); }
    }
    public bool HasProject => Folder is not null;
    public bool NoProject => !HasProject;
    public string ProjectName => Folder is null ? "NO FOLDER OPEN" : Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar));
    public DiffTab? ActiveDiff => ActiveTab as DiffTab;
    public bool HasEditor => HasDocument || ActiveDiff is not null;
    private bool gitVisible;
    public bool ShowGitPanel { get => gitVisible; set { gitVisible = value; if (value) ShowOutlinePanel = false; Notify(); Notify(nameof(ShowProjectPanel)); } }
    private bool outlineVisible;
    public bool ShowOutlinePanel { get => outlineVisible; set { outlineVisible = value; if (value) ShowGitPanel = false; Notify(); Notify(nameof(ShowProjectPanel)); } }
    public bool ShowProjectPanel => !ShowGitPanel && !ShowOutlinePanel;
    private string branch = "";
    public string Branch { get => branch; set { branch = value; Notify(); Notify(nameof(ProjectHeader)); } }
    public string ProjectHeader => Folder is null ? "Open Recent Project" : ProjectName + (Branch.Length > 0 ? "  /  " + Branch : "");
    public EditorTab? ActiveDocument => ActiveTab as EditorTab;
    public SearchTab? ActiveSearch => ActiveTab as SearchTab;
    public bool HasSearch => ActiveSearch is not null;
    public DiagnosticsTab? ActiveDiagnostics => ActiveTab as DiagnosticsTab;
    public bool HasDiagnostics => ActiveDiagnostics is not null;
    private string codeStatus = "No language server";
    public string CodeStatus { get => codeStatus; set { codeStatus = value; Notify(); } }
    private string diagnosticStatus = "0 errors · 0 warnings";
    public string DiagnosticStatus { get => diagnosticStatus; set { diagnosticStatus = value; Notify(); } }
    private string currentSymbol = "";
    public string CurrentSymbol { get => currentSymbol; set { if (currentSymbol == value) return; currentSymbol = value; Notify(); Notify(nameof(Breadcrumb)); } }
    public string Breadcrumb => (ActiveDocument?.FilePath is string path
        ? (Folder is null ? path : Path.GetRelativePath(Folder, path)) : ActiveDiff?.Name ?? "untitled") + (currentSymbol.Length == 0 ? "" : " › " + currentSymbol);
    public bool HasDocument => ActiveDocument is not null;
    public bool ShowWelcome => ActiveTab is null;
    private string status = "Ready";
    public string Status { get => status; set { status = value; Notify(); } }
    private string caret = "Ln 1, Col 1";
    public string Caret { get => caret; set { caret = value; Notify(); } }
    private string language = "Plain text";
    public string Language { get => language; set { language = value; Notify(); } }
    public void RefreshDocumentDetails() { Notify(nameof(Breadcrumb)); }
}

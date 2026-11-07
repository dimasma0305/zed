using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit.Highlighting;
using Avalonia.Threading;
using Avalonia.Media;
using AvaloniaEdit.Search;
using Zyber.Models;
using Zyber.Languages;
using Zyber.Services;
using Zyber.Views;
using Avalonia.VisualTree;

namespace Zyber;

public sealed partial class MainWindow : Window
{
    public WorkspaceState State { get; } = new();
    public LanguageFeatures Code { get; }
    private readonly SearchPanel searchPanel;
    private EditorTab? displayedTab;
    private bool busy;
    private bool closeApproved;
    private readonly RecentProjects recentProjects;
    private readonly WorkspaceWatcher watcher = new();
    private readonly DispatcherTimer watchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool refreshingFiles;
    private bool closed;
    private TerminalDock? terminalDock;
    private double terminalHeight = 320;
    private readonly List<string> navigation = [];
    private int navigationIndex = -1;
    private bool navigatingHistory;
    private bool projectShortcutPending;
    private DateTimeOffset projectShortcutStarted;

    public MainWindow() : this([]) { }

    public MainWindow(string[] arguments, RecentProjects? recentProjects = null, Func<LanguageDefinition, LanguageServerConfiguration?>? languageServers = null)
    {
        this.recentProjects = recentProjects ?? new RecentProjects(null);
        InitializeComponent();
        DataContext = State;
        GitSidebar.DiffRequested += (_, diff) => OpenDiff(diff);
        GitSidebar.RepositoryChanged += (_, _) => State.Branch = GitSidebar.State.Branch;
        watcher.Changed += (_, _) => Dispatcher.UIThread.Post(() => { if (!closed) { watchDebounce.Stop(); watchDebounce.Start(); } });
        watcher.Failed += (_, message) => Dispatcher.UIThread.Post(() => { if (!closed) State.Status = "File watcher: " + message; });
        watchDebounce.Tick += async (_, _) => { watchDebounce.Stop(); await RefreshFilesFromDiskAsync(); };
        State.Tabs.CollectionChanged += (_, _) => UpdateWatchedPaths();
        Activated += async (_, _) => await RefreshFilesFromDiskAsync();
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        foreach (var margin in Editor.TextArea.LeftMargins.ToArray())
        {
            if (margin is AvaloniaEdit.Editing.LineNumberMargin) { margin.MinWidth = 42; margin.Margin = new Thickness(0, 0, 24, 0); }
            else Editor.TextArea.LeftMargins.Remove(margin);
        }
        searchPanel = SearchPanel.Install(Editor);
        Editor.TextArea.TextView.LineTransformers.Add(new DiffColorizer(() => State.ActiveDiff is not null, () => (Application.Current as App)?.EditorTheme));
        Code = new LanguageFeatures(this, Editor, languageServers);
        OutlineSidebar.Attach(this, Editor);
        DiagnosticsPanel.ResultRequested += async (_, item) => await RunAsync(() => Code.OpenLocationAsync(new LspLocation(LspText.FileUri(item.Path), item.Diagnostic.Range)));
        ApplyEditorTheme();
        if (Application.Current is App application)
        {
            application.EditorThemeChanged += ThemeChanged;
            if (application.ThemeWarning is string warning) State.Status = warning;
            Closed += (_, _) => application.EditorThemeChanged -= ThemeChanged;
        }
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();
        State.PropertyChanged += StateChanged;
        ProjectSearchPanel.ResultRequested += async (_, hit) => await RunAsync(() => OpenSearchResultAsync(hit));
        Closed += async (_, _) => { OutlineSidebar.Dispose(); GitSidebar.Dispose(); await Code.DisposeAsync(); closed = true; watchDebounce.Stop(); watcher.Dispose(); foreach (var search in State.Tabs.OfType<SearchTab>()) search.Dispose(); terminalDock?.Dispose(); };
        AddHandler(KeyDownEvent, HandleShortcut, RoutingStrategies.Tunnel);
        Closing += WindowClosing;
        Opened += async (_, _) => await RunAsync(async () =>
        {
            foreach (var path in arguments)
            {
                if (Directory.Exists(path)) await OpenFolderAsync(path);
                else if (File.Exists(path)) await OpenDocumentAsync(path);
            }
        });
    }

    public void NewDocument()
    {
        var tab = new EditorTab();
        State.Tabs.Add(tab);
        State.ActiveTab = tab;
    }

    public async Task OpenDocumentAsync(string path)
    {
        var snapshot = await TextFileStore.ReadAsync(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var existing = State.Tabs.OfType<EditorTab>().FirstOrDefault(tab => string.Equals(tab.FilePath, snapshot.Path, comparison));
        if (existing is not null) { State.ActiveTab = existing; RecordNavigation(snapshot.Path); return; }
        var document = new EditorTab(snapshot);
        State.Tabs.Add(document);
        State.ActiveTab = document;
        State.Status = "Opened " + document.Name;
        RecordNavigation(snapshot.Path);
    }

    public async Task OpenFolderAsync(string path)
    {
        var absolutePath = Path.GetFullPath(path);
        var entries = await Task.Run(() => FileNode.ReadDirectory(absolutePath));
        State.Files.Clear();
        foreach (var entry in entries) State.Files.Add(entry);
        State.Folder = absolutePath;
        State.Branch = "";
        UpdateWatchedPaths();
        await GitSidebar.SetProjectAsync(absolutePath);
        State.Status = absolutePath;
        Title = State.ProjectName + " — Zyber";
        if (terminalDock is not null) terminalDock.StartingDirectory = absolutePath;
        try { await recentProjects.AddAsync(absolutePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { State.Status = "Project opened; recent-project history could not be saved: " + exception.Message; }
    }

    private void UpdateWatchedPaths()
    {
        if (!closed) watcher.Watch(State.Folder, State.Tabs.OfType<EditorTab>().Select(tab => tab.FilePath).OfType<string>());
    }

    public async Task RefreshFilesFromDiskAsync()
    {
        if (closed) return;
        if (refreshingFiles) { watchDebounce.Start(); return; }
        refreshingFiles = true;
        try
        {
            foreach (var tab in State.Tabs.OfType<EditorTab>().Where(tab => tab.FilePath is not null).ToArray())
            {
                var expected = tab.Fingerprint;
                try
                {
                    var snapshot = await TextFileStore.ReadAsync(tab.FilePath!);
                    if (closed) return;
                    if (!State.Tabs.Contains(tab) || tab.Fingerprint != expected) continue;
                    if (snapshot.Fingerprint == expected) { tab.HasExternalChanges = false; continue; }
                    var caret = displayedTab == tab ? Editor.CaretOffset : tab.CaretOffset;
                    if (tab.ReloadIfUnchanged(snapshot, expected))
                    {
                        if (displayedTab == tab) Editor.CaretOffset = Math.Min(caret, tab.Document.TextLength);
                        State.Status = "Reloaded " + tab.Name + " after a change on disk";
                    }
                    else
                    {
                        tab.HasExternalChanges = true;
                        State.Status = tab.Name + " changed on disk; your unsaved edits are preserved. Save As to keep both versions.";
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    tab.HasExternalChanges = true;
                    State.Status = tab.Name + ": " + exception.Message + " The open buffer has been preserved.";
                }
            }
            if (!closed && State.Folder is string folder) await FileNode.RefreshAsync(State.Files, folder, () => !closed && State.Folder == folder);
            if (!closed) GitSidebar.QueueRefresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!closed) State.Status = "File refresh failed: " + exception.Message;
        }
        finally { refreshingFiles = false; }
    }

    private void StateChanged(object? sender, PropertyChangedEventArgs arguments)
    {
        if (arguments.PropertyName != nameof(WorkspaceState.ActiveTab)) return;
        var tab = State.ActiveDocument;
        var nextDocument = tab?.Document ?? State.ActiveDiff?.Document;
        if (displayedTab != tab || Editor.Document != nextDocument)
        {
            if (displayedTab is not null)
            {
                displayedTab.CaretOffset = Editor.CaretOffset;
                displayedTab.VerticalOffset = Editor.VerticalOffset;
            }
            Editor.Document = nextDocument;
            displayedTab = tab;
            if (tab is not null)
            {
                Editor.CaretOffset = Math.Min(tab.CaretOffset, tab.Document.TextLength);
                Editor.ScrollToVerticalOffset(tab.VerticalOffset);
            }
        }
        Editor.IsReadOnly = State.ActiveDiff is not null;
        Editor.SyntaxHighlighting = GetHighlighting(State.ActiveDiff is not null ? "changes.diff" : tab?.FilePath);
        State.Language = Editor.SyntaxHighlighting?.Name ?? "Plain text";
        UpdateCaret();
        if (tab is not null) Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
        else if (State.ActiveSearch is not null) Dispatcher.UIThread.Post(ProjectSearchPanel.FocusQuery, DispatcherPriority.Input);
    }

    private IHighlightingDefinition? GetHighlighting(string? path)
    {
        var definition = path is null ? null : HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
        if (definition is null) return null;
        foreach (var token in definition.NamedHighlightingColors)
        {
            var name = token.Name.ToLowerInvariant();
            var category = name.Contains("comment") ? "comment"
                : name.Contains("string") || name.Contains("char") ? "string"
                : name.Contains("fieldname") || name.Contains("property") ? "property"
                : name is "bool" or "truefalse" ? "boolean"
                : name.Contains("method") || name.Contains("function") ? "function"
                : name.Contains("punctuation") ? "punctuation.bracket"
                : name.Contains("number") || name.Contains("digit") ? "number"
                : name.Contains("type") || name.Contains("class") ? "type"
                : name.Contains("keyword") || name.Contains("modifier") || name.Contains("visibility") ? "keyword"
                : "primary";
            if (Application.Current is App application)
            {
                token.Foreground = new SimpleHighlightingBrush(application.EditorTheme.SyntaxColor(category));
                token.FontWeight = null;
                token.FontStyle = null;
                token.Background = null;
                if (application.EditorTheme.SyntaxStyle(category) is { } style)
                {
                    if (style.TryGetProperty("background_color", out var background) && background.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        try { token.Background = new SimpleHighlightingBrush(ZedTheme.ParseColor(background.GetString()!)); }
                        catch (FormatException) { /* Zed ignores invalid optional colors. */ }
                    }
                    if (style.TryGetProperty("font_style", out var fontStyle) && fontStyle.ValueKind == System.Text.Json.JsonValueKind.String)
                        token.FontStyle = fontStyle.GetString() switch { "italic" => FontStyle.Italic, "oblique" => FontStyle.Oblique, _ => FontStyle.Normal };
                    if (style.TryGetProperty("font_weight", out var weight) && weight.ValueKind == System.Text.Json.JsonValueKind.Number && weight.TryGetInt32(out var number) && number is >= 100 and <= 900)
                        token.FontWeight = (FontWeight)number;
                }
            }
        }
        return definition;
    }

    private void ThemeChanged(object? sender, EventArgs arguments) => ApplyEditorTheme();

    private void ApplyEditorTheme()
    {
        if (Application.Current is not App application) return;
        var theme = application.EditorTheme;
        Editor.TextArea.SelectionBrush = new SolidColorBrush(theme.PlayerColor("selection"));
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.Caret.CaretBrush = new SolidColorBrush(theme.PlayerColor("cursor"));
        Editor.TextArea.TextView.CurrentLineBackground = theme.Brush("editor.active_line.background");
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        Editor.Options.LineHeightFactor = 1;
        Editor.Options.LineHeightFactor = Editor.FontSize * 1.618 / Editor.TextArea.TextView.DefaultLineHeight;
        searchPanel.Background = theme.Brush("elevated_surface.background");
        searchPanel.BorderBrush = theme.Brush("border");
        Editor.SearchResultsBrush = theme.Brush("search.match_background");
        searchPanel.SetSearchResultsBrush(theme.Brush("search.match_background"));
        Editor.SyntaxHighlighting = GetHighlighting(State.ActiveDiff is not null ? "changes.diff" : State.ActiveDocument?.FilePath);
        Code.RefreshDiagnostics();
    }

    private void UpdateCaret() => State.Caret = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";

    private async Task PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open files", AllowMultiple = true });
        foreach (var file in files)
            await OpenDocumentAsync(file.TryGetLocalPath() ?? throw new IOException("Only local files are supported in this version."));
    }

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open project folder" });
        if (folders.Count > 0)
            await OpenFolderAsync(folders[0].TryGetLocalPath() ?? throw new IOException("Only local folders are supported in this version."));
    }

    private async Task<bool> SaveAsync(EditorTab tab, bool saveAs = false)
    {
        var path = tab.FilePath;
        var expected = tab.Fingerprint;
        if (path is null || saveAs)
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Save document", SuggestedFileName = tab.Name, ShowOverwritePrompt = false });
            if (destination is null) return false;
            path = destination.TryGetLocalPath() ?? throw new IOException("Only local files are supported in this version.");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (State.Tabs.OfType<EditorTab>().Any(other => other != tab && string.Equals(other.FilePath, path, comparison)))
                throw new IOException("This file is already open in another tab. Choose a different destination.");
            if (!string.Equals(path, tab.FilePath, comparison))
            {
                expected = await TextFileStore.FingerprintAsync(path);
                if (expected is not null && !await Dialogs.ConfirmReplaceAsync(this, path)) return false;
            }
        }
        var text = tab.Document.Text;
        var fingerprint = await TextFileStore.WriteAsync(path, text, tab.Encoding, expected);
        tab.RecordSave(path, fingerprint, text);
        UpdateWatchedPaths();
        State.RefreshDocumentDetails();
        StateChanged(this, new(nameof(WorkspaceState.ActiveTab)));
        State.Status = "Saved " + tab.Name;
        Dispatcher.UIThread.Post(async () => await Code.SavedAsync(tab));
        return true;
    }

    private async Task<bool> CanCloseAsync(EditorTab tab)
    {
        if (!tab.IsDirty) return true;
        var choice = await Dialogs.ConfirmUnsavedAsync(this, tab.Name);
        return choice switch
        {
            UnsavedChoice.Discard => true,
            UnsavedChoice.Save => await SaveAsync(tab) && !tab.IsDirty,
            _ => false
        };
    }

    private async Task CloseTabAsync(WorkspaceTab tab)
    {
        if (tab is EditorTab document && !await CanCloseAsync(document)) return;
        if (tab is IDisposable disposable) disposable.Dispose();
        var position = State.Tabs.IndexOf(tab);
        var active = State.ActiveTab == tab;
        State.Tabs.Remove(tab);
        if (active) State.ActiveTab = State.Tabs.Count == 0 ? null : State.Tabs[Math.Clamp(position, 0, State.Tabs.Count - 1)];
    }

    private async void WindowClosing(object? sender, WindowClosingEventArgs arguments)
    {
        if (closeApproved) return;
        if (busy) { arguments.Cancel = true; return; }
        if (!State.Tabs.Any(tab => tab.IsDirty))
        {
            arguments.Cancel = true;
            await Code.DisposeAsync();
            closeApproved = true;
            Close();
            return;
        }
        arguments.Cancel = true;
        var approved = false;
        await RunAsync(async () =>
        {
            foreach (var tab in State.Tabs.OfType<EditorTab>().ToArray())
                if (!await CanCloseAsync(tab)) return;
            approved = true;
        });
        if (approved) { await Code.DisposeAsync(); closeApproved = true; Close(); }
    }

    public async Task ShowThemesAsync()
    {
        if (Application.Current is not App application) return;
        await application.ReloadThemesAsync();
        try
        {
            var selected = await Dialogs.ChooseThemeAsync(this, application.Themes.Themes, application.EditorTheme.Name, application.PreviewTheme);
            if (selected is not null) await application.SelectThemeAsync(selected);
            State.Status = selected is null ? "Theme unchanged" : "Theme: " + selected.Name;
            application.CancelThemePreview();
            var warnings = application.Themes.Warnings;
            if (warnings.Count > 0) await Dialogs.ShowErrorAsync(this, string.Join("\n", warnings.Take(8)));
        }
        finally { application.CancelThemePreview(); }
    }

    private async Task ImportThemeAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Zed theme", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Zed theme JSON") { Patterns = ["*.json"] }]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not string path) return;
        try { await ThemeCatalog.ImportAsync(path); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new IOException("Could not import this Zed theme: " + exception.Message, exception); }
        await ShowThemesAsync();
    }

    private async Task ShowCommandsAsync()
    {
        var commands = new EditorCommand[]
        {
            new("Theme selector: Toggle", "", ShowThemesAsync),
            new("Theme: Import Zed theme…", "", ImportThemeAsync),
            new("File: New file", "Ctrl N", () => { NewDocument(); return Task.CompletedTask; }),
            new("File: Open file", "Ctrl O", PickFileAsync),
            new("Project: Open folder", "Ctrl Shift O", PickFolderAsync),
            new("File: Find file", "Ctrl P", ShowFileFinderAsync),
            new("Project: Search", "Ctrl Shift F", ShowProjectSearchAsync),
            new("File: Save", "Ctrl S", async () => { if (State.ActiveDocument is { } tab) await SaveAsync(tab); }),
            new("File: Save as…", "Ctrl Shift S", async () => { if (State.ActiveDocument is { } tab) await SaveAsync(tab, true); }),
            new("File: Close tab", "Ctrl W", async () => { if (State.ActiveTab is { } tab) await CloseTabAsync(tab); }),
            new("Editor: Find in file", "Ctrl F", () => { searchPanel.Open(); searchPanel.Reactivate(); return Task.CompletedTask; }),
            new("Editor: Toggle word wrap", "", () => { Editor.WordWrap = !Editor.WordWrap; return Task.CompletedTask; }),
            new("Project: Refresh file tree", "", RefreshFilesFromDiskAsync),
            new("Outline: Toggle focus", "Ctrl Shift B", ToggleOutlineAsync),
            new("Git: Toggle Git panel", "Ctrl Shift G", ToggleGitPanelAsync),
            new("Language Server: Restart", "", Code.RestartAsync),
            new("Editor: Show completions", "Ctrl Space", Code.ShowCompletionsAsync),
            new("Editor: Go to definition", "F12", () => Code.GoToDefinitionAsync()),
            new("Editor: Find all references", "Shift Alt F12", () => Code.GoToDefinitionAsync(true)),
            new("Editor: Hover", "Ctrl K Ctrl I", Code.ShowHoverAsync),
            new("Editor: Format", "Shift Alt F", Code.FormatAsync),
            new("Diagnostics: Open project diagnostics", "Ctrl Shift M", Code.ShowDiagnosticsAsync),
            new("Terminal: Toggle terminal", "Ctrl `", ToggleTerminalAsync),
            new("Terminal: Open external terminal", "", OpenExternalTerminalAsync)
        };
        var selected = await Dialogs.ChooseCommandAsync(this, commands);
        if (selected is not null) await selected.Execute();
    }

    public async Task OpenSearchResultAsync(SearchHit hit)
    {
        await OpenDocumentAsync(hit.FullPath);
        var document = State.ActiveDocument!;
        Editor.Select(Math.Min(hit.Offset, document.Document.TextLength), Math.Min(hit.Length, Math.Max(0, document.Document.TextLength - hit.Offset)));
        Editor.ScrollTo(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
        if (document.Fingerprint != hit.Fingerprint || document.IsDirty)
            State.Status = "File changed since this search; rerun the search to refresh its locations.";
    }

    public Task ShowProjectSearchAsync()
    {
        if (State.Folder is not string root) throw new IOException("Open a project folder before searching.");
        var search = State.Tabs.OfType<SearchTab>().FirstOrDefault(tab => ProjectFiles.PathComparer.Equals(tab.Root, root));
        if (search is null) { search = new SearchTab(root); State.Tabs.Add(search); }
        State.ActiveTab = search;
        return Task.CompletedTask;
    }

    private async Task ShowFileFinderAsync()
    {
        if (State.Folder is not string root) { await PickFileAsync(); return; }
        State.Status = "Indexing project files…";
        var index = await ProjectFiles.IndexAsync(root);
        State.Status = index.LimitReached ? "File index limit reached; some files are not listed."
            : index.Warnings.Count > 0 ? string.Join(" · ", index.Warnings) : $"{index.Files.Count} project files";
        var result = await Dialogs.ChooseFileAsync(this, index);
        if (result is null) return;
        await OpenDocumentAsync(result.File.FullPath);
        if (result.Line is int line && State.ActiveDocument is { } document)
        {
            var target = document.Document.GetLineByNumber(Math.Clamp(line, 1, document.Document.LineCount));
            Editor.CaretOffset = target.Offset + Math.Clamp((result.Column ?? 1) - 1, 0, target.Length);
            Editor.ScrollTo(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
        }
    }

    private void ActivateAdjacentTab(int direction)
    {
        if (State.Tabs.Count == 0) return;
        var index = State.ActiveTab is null ? 0 : State.Tabs.IndexOf(State.ActiveTab);
        State.ActiveTab = State.Tabs[(index + direction + State.Tabs.Count) % State.Tabs.Count];
    }

    private Task OpenExternalTerminalAsync()
    {
        var directory = State.Folder ?? (State.ActiveDocument?.FilePath is string path ? Path.GetDirectoryName(path)! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows()) start = new("powershell.exe") { UseShellExecute = true, WorkingDirectory = directory };
        else if (OperatingSystem.IsMacOS())
        {
            start = new("open") { UseShellExecute = false };
            start.ArgumentList.Add("-a"); start.ArgumentList.Add("Terminal"); start.ArgumentList.Add(directory);
        }
        else start = new("x-terminal-emulator") { UseShellExecute = false, WorkingDirectory = directory };
        using var process = Process.Start(start) ?? throw new IOException("The terminal could not be started.");
        State.Status = "Opened external terminal";
        return Task.CompletedTask;
    }

    private async void HandleShortcut(object? sender, KeyEventArgs arguments)
    {
        var commandModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (terminalDock?.IsKeyboardFocusWithin != true && State.HasDocument)
        {
            if (arguments.Key == Key.F12 && arguments.KeyModifiers is KeyModifiers.None or (KeyModifiers.Shift | KeyModifiers.Alt))
            {
                arguments.Handled = true;
                await RunAsync(() => Code.GoToDefinitionAsync(arguments.KeyModifiers.HasFlag(KeyModifiers.Shift)));
                return;
            }
            if (arguments.Key == Key.F && arguments.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Alt))
            {
                arguments.Handled = true;
                await RunAsync(Code.FormatAsync);
                return;
            }
        }
        if (!arguments.KeyModifiers.HasFlag(commandModifier)) return;
        var shift = arguments.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (arguments.Key == Key.Oem3)
        {
            arguments.Handled = true;
            await RunAsync(ToggleTerminalAsync);
            return;
        }
        if (terminalDock?.IsKeyboardFocusWithin == true && !(arguments.Key == Key.P && shift)) return;
        if (projectShortcutPending)
        {
            projectShortcutPending = false;
            if (arguments.Key == Key.I && DateTimeOffset.UtcNow - projectShortcutStarted < TimeSpan.FromSeconds(3))
            {
                arguments.Handled = true;
                await RunAsync(Code.ShowHoverAsync);
                return;
            }
            if (arguments.Key == Key.O && DateTimeOffset.UtcNow - projectShortcutStarted < TimeSpan.FromSeconds(3))
            {
                arguments.Handled = true;
                await RunAsync(PickFolderAsync);
                return;
            }
        }
        if (arguments.Key == Key.K && !shift)
        {
            projectShortcutPending = true; projectShortcutStarted = DateTimeOffset.UtcNow;
            arguments.Handled = true;
            return;
        }
        Func<Task>? action = arguments.Key switch
        {
            Key.N => () => { NewDocument(); return Task.CompletedTask; },
            Key.O when shift => PickFolderAsync,
            Key.O => PickFileAsync,
            Key.S => async () => { if (State.ActiveDocument is { } tab) await SaveAsync(tab, shift); },
            Key.W => async () => { if (State.ActiveTab is { } tab) await CloseTabAsync(tab); },
            Key.P when shift => ShowCommandsAsync,
            Key.Space => Code.ShowCompletionsAsync,
            Key.M when shift => Code.ShowDiagnosticsAsync,
            Key.G when shift => ToggleGitPanelAsync,
            Key.B when shift => ToggleOutlineAsync,
            Key.P or Key.E => ShowFileFinderAsync,
            Key.F when shift => ShowProjectSearchAsync,
            Key.PageUp => () => { ActivateAdjacentTab(-1); return Task.CompletedTask; },
            Key.PageDown => () => { ActivateAdjacentTab(1); return Task.CompletedTask; },
            _ => null
        };
        if (action is null) return;
        arguments.Handled = true;
        await RunAsync(action);
    }

    public Task ToggleTerminalAsync()
    {
        if (TerminalContainer.IsVisible) { HideTerminal(); return Task.CompletedTask; }
        if (terminalDock is null)
        {
            terminalDock = new TerminalDock { StartingDirectory = State.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            terminalDock.HideRequested += (_, _) => HideTerminal();
            TerminalContainer.Content = terminalDock;
        }
        TerminalContainer.IsVisible = true;
        TerminalSplitter.IsVisible = true;
        EditorDockLayout.RowDefinitions[1].Height = new GridLength(1);
        EditorDockLayout.RowDefinitions[2].Height = new GridLength(terminalHeight);
        if (terminalDock.Items.Count == 0) terminalDock.AddSession();
        Dispatcher.UIThread.Post(() => terminalDock.ActiveTerminal?.Focus(), DispatcherPriority.Input);
        return Task.CompletedTask;
    }

    private void HideTerminal()
    {
        if (EditorDockLayout.RowDefinitions[2].ActualHeight > 0) terminalHeight = EditorDockLayout.RowDefinitions[2].ActualHeight;
        TerminalContainer.IsVisible = false;
        TerminalSplitter.IsVisible = false;
        EditorDockLayout.RowDefinitions[1].Height = new GridLength(0);
        EditorDockLayout.RowDefinitions[2].Height = new GridLength(0);
        if (State.ActiveDocument is not null) Editor.TextArea.Focus();
    }

    private void RecordNavigation(string path)
    {
        if (navigatingHistory || (navigationIndex >= 0 && ProjectFiles.PathComparer.Equals(navigation[navigationIndex], path))) return;
        if (navigationIndex + 1 < navigation.Count) navigation.RemoveRange(navigationIndex + 1, navigation.Count - navigationIndex - 1);
        navigation.Add(path);
        if (navigation.Count > 200) navigation.RemoveAt(0);
        navigationIndex = navigation.Count - 1;
    }

    private async Task NavigateAsync(int direction)
    {
        var target = navigationIndex + direction;
        if (target < 0 || target >= navigation.Count) return;
        navigatingHistory = true;
        try { await OpenDocumentAsync(navigation[target]); navigationIndex = target; }
        finally { navigatingHistory = false; }
    }

    private async Task ShowRecentProjectsAsync()
    {
        var commands = new List<EditorCommand> { new("Open Project…", "Ctrl-K Ctrl-O", PickFolderAsync) };
        commands.AddRange((await recentProjects.ReadAsync()).Where(Directory.Exists).Select(path => new EditorCommand(path, "", () => OpenFolderAsync(path))));
        var selected = await Dialogs.ChooseCommandAsync(this, commands);
        if (selected is not null) await selected.Execute();
    }

    private async Task CloneRepositoryAsync()
    {
        var repository = await Dialogs.PromptAsync(this, "Clone Repository", "Repository URL or local repository path");
        if (repository is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose parent folder for the clone" });
        if (folders.Count == 0) return;
        var parent = folders[0].TryGetLocalPath() ?? throw new IOException("Choose a local folder.");
        var proposed = Path.GetFileName(repository.TrimEnd('/', '\\'));
        if (proposed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) proposed = proposed[..^4];
        var name = await Dialogs.PromptAsync(this, "Clone Repository", "Name of the new folder", proposed);
        if (name is null) return;
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new IOException("Enter a single folder name without path separators.");
        var destination = Path.Combine(parent, name);
        State.Status = "Cloning repository…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await GitOperations.CloneAsync(repository, destination, timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw new IOException("Clone timed out. The destination was left in place for inspection."); }
        await OpenFolderAsync(destination);
    }

    private void TitlePointerPressed(object? sender, PointerPressedEventArgs arguments)
    {
        if (arguments.Source is Visual visual && visual.GetVisualAncestors().OfType<Button>().Any()) return;
        if (arguments.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(arguments);
    }
    private void TitleDoubleTapped(object? sender, TappedEventArgs arguments)
    {
        if (arguments.Source is Visual visual && visual.GetVisualAncestors().OfType<Button>().Any()) return;
        ToggleMaximize();
    }
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void MinimizeClicked(object? sender, RoutedEventArgs arguments) => WindowState = WindowState.Minimized;
    private void MaximizeClicked(object? sender, RoutedEventArgs arguments) => ToggleMaximize();
    private void CloseWindowClicked(object? sender, RoutedEventArgs arguments) => Close();
    private void ThreadsSidebarClicked(object? sender, RoutedEventArgs arguments) => WorkspaceLayout.ColumnDefinitions[0].Width = new GridLength(WorkspaceLayout.ColumnDefinitions[0].ActualWidth > 1 ? 0 : 300);
    private void ProjectPanelClicked(object? sender, RoutedEventArgs arguments)
    {
        var hide = State.ShowProjectPanel && WorkspaceLayout.ColumnDefinitions[4].ActualWidth > 1;
        State.ShowGitPanel = false;
        State.ShowOutlinePanel = false;
        WorkspaceLayout.ColumnDefinitions[4].Width = new GridLength(hide ? 0 : 240);
    }
    public async Task ToggleOutlineAsync()
    {
        if (State.ShowOutlinePanel && OutlineSidebar.IsKeyboardFocusWithin) { Editor.TextArea.Focus(); return; }
        State.ShowOutlinePanel = true;
        WorkspaceLayout.ColumnDefinitions[4].Width = new GridLength(300);
        await OutlineSidebar.RefreshAsync();
        OutlineSidebar.FocusQuery();
    }
    private async void OutlineClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(ToggleOutlineAsync);
    private Task ToggleGitPanelAsync()
    {
        var hide = State.ShowGitPanel && WorkspaceLayout.ColumnDefinitions[4].ActualWidth > 1;
        State.ShowGitPanel = true;
        WorkspaceLayout.ColumnDefinitions[4].Width = new GridLength(hide ? 0 : 360);
        if (!hide) GitSidebar.QueueRefresh();
        return Task.CompletedTask;
    }
    public void OpenDiff(GitDiffRequest diff)
    {
        var tab = new DiffTab(diff.Title, diff.Text);
        State.Tabs.Add(tab);
        State.ActiveTab = tab;
    }
    private async void GitPanelClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(ToggleGitPanelAsync);
    private void FindInFileClicked(object? sender, RoutedEventArgs arguments) { if (State.HasEditor) { searchPanel.Open(); searchPanel.Reactivate(); } }
    private async void BackClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(() => NavigateAsync(-1));
    private async void ForwardClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(() => NavigateAsync(1));
    private async void RecentProjectsClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(ShowRecentProjectsAsync);
    private async void CloneRepositoryClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(CloneRepositoryAsync);
    private async void FindFileClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(ShowFileFinderAsync);
    private async void DiagnosticsClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(Code.ShowDiagnosticsAsync);
    private async void ProjectSearchClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(ShowProjectSearchAsync);

    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        try { await action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            State.Status = exception.Message;
            await Dialogs.ShowErrorAsync(this, exception.Message);
        }
        finally { busy = false; }
    }

    private void NewFileClicked(object? sender, RoutedEventArgs arguments) { if (!busy) NewDocument(); }
    private async void OpenFileClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(PickFileAsync);
    private async void OpenFolderClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(PickFolderAsync);
    private async void CommandPaletteClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(ShowCommandsAsync);
    private async void TerminalClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(ToggleTerminalAsync);
    private async void RefreshClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(RefreshFilesFromDiskAsync);
    private async void CloseTabClicked(object? sender, RoutedEventArgs arguments)
    {
        arguments.Handled = true;
        if (sender is Button { DataContext: WorkspaceTab tab }) await RunAsync(() => CloseTabAsync(tab));
    }
    private async void FileSelected(object? sender, SelectionChangedEventArgs arguments)
    {
        if (Files.SelectedItem is FileNode { IsDirectory: false, FullPath: string path }) await RunAsync(() => OpenDocumentAsync(path));
    }
}

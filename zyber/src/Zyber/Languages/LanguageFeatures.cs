using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Zyber.Models;
using Zyber.Services;

namespace Zyber.Languages;

public sealed class LanguageFeatures : IAsyncDisposable
{
    private sealed class Binding(EditorTab tab, string path, LanguageServerSession session, string text)
    {
        public EditorTab Tab { get; } = tab;
        public string Path { get; } = path;
        public LanguageServerSession Session { get; } = session;
        public string Text { get; set; } = text;
        public int Version { get; set; } = 1;
        public SemaphoreSlim Changes { get; } = new(1, 1);
        public IReadOnlyList<LspDiagnostic> Diagnostics { get; set; } = [];
    }
    private readonly MainWindow window;
    private readonly TextEditor editor;
    private readonly Func<LanguageDefinition, LanguageServerConfiguration?> resolve;
    private readonly Dictionary<EditorTab, Task<Binding?>> documents = [];
    private readonly Dictionary<string, Task<LanguageServerSession>> sessions = [];
    private readonly HashSet<EditorTab> subscribed = [];
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DiagnosticRenderer renderer = new();
    private CancellationTokenSource lifetime = new();
    private bool restarting;
    private int generation;
    private readonly DispatcherTimer autoComplete = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? request;
    private CompletionWindow? completionWindow;
    private bool disposed;
    public DiagnosticsTab Diagnostics { get; } = new();
    public IReadOnlyList<LspCompletion> LastCompletions { get; private set; } = [];

    public LanguageFeatures(MainWindow window, TextEditor editor, Func<LanguageDefinition, LanguageServerConfiguration?>? resolve = null)
    {
        this.window = window;
        this.editor = editor;
        this.resolve = resolve ?? (language => LanguageServerRegistry.Resolve(language));
        editor.TextArea.TextView.BackgroundRenderers.Add(renderer);
        window.State.Tabs.CollectionChanged += TabsChanged;
        window.State.PropertyChanged += StateChanged;
        debounce.Tick += async (_, _) => { debounce.Stop(); await SynchronizeAllAsync(); };
        editor.TextArea.Caret.PositionChanged += (_, _) => ToolTip.SetIsOpen(editor, false);
        autoComplete.Tick += async (_, _) => { autoComplete.Stop(); if (!disposed && !restarting && completionWindow is null) await ShowCompletionsAsync(); };
        editor.TextArea.TextEntered += (_, arguments) =>
        {
            if (disposed || restarting || completionWindow is not null || string.IsNullOrEmpty(arguments.Text)) return;
            var last = arguments.Text[^1];
            if (char.IsLetterOrDigit(last) || last is '_' or '.') { autoComplete.Stop(); autoComplete.Start(); }
        };
    }

    private async void TabsChanged(object? sender, NotifyCollectionChangedEventArgs arguments)
    {
        if (disposed || restarting) return;
        foreach (var tab in window.State.Tabs.OfType<EditorTab>().ToArray())
            if (subscribed.Add(tab)) { tab.Document.TextChanged += DocumentChanged; await ConnectSafelyAsync(tab); }
        foreach (var tab in subscribed.Where(tab => !window.State.Tabs.Contains(tab)).ToArray())
        {
            tab.Document.TextChanged -= DocumentChanged;
            subscribed.Remove(tab);
            if (documents.Remove(tab, out var pending))
                try
                {
                    if (await pending is { } binding) await binding.Session.CloseAsync(binding.Path, lifetime.Token);
                }
                catch (Exception exception) when (Expected(exception)) { Report(exception); }
        }
        RefreshDiagnostics();
    }
    private async Task<Binding?> GetBindingAsync(EditorTab tab)
    {
        if (disposed || restarting || tab.FilePath is null) return null;
        if (!documents.TryGetValue(tab, out var pending)) documents[tab] = pending = ConnectAsync(tab);
        return await pending;
    }
    private async Task ConnectSafelyAsync(EditorTab tab)
    {
        try { await GetBindingAsync(tab); }
        catch (Exception exception) when (Expected(exception)) { Report(exception); }
    }
    private async Task<Binding?> ConnectAsync(EditorTab tab)
    {
        var currentGeneration = generation;
        var path = tab.FilePath!;
        var language = LanguageServerRegistry.ForFile(path);
        if (language is null) return null;
        var configuration = resolve(language);
        if (configuration is null) { window.State.CodeStatus = language.Name + ": " + language.Server + " not installed"; return null; }
        var root = window.State.Folder ?? Path.GetDirectoryName(path)!;
        var key = configuration.Name + "\0" + root;
        if (!sessions.TryGetValue(key, out var startup)) sessions[key] = startup = StartAsync(configuration, root);
        window.State.CodeStatus = "Starting " + configuration.Name + "…";
        var session = await startup;
        if (disposed || generation != currentGeneration || !window.State.Tabs.Contains(tab) || tab.FilePath != path) return null;
        var binding = new Binding(tab, path, session, tab.Document.Text);
        await session.OpenAsync(path, language.Id, binding.Text, binding.Version, lifetime.Token);
        window.State.CodeStatus = configuration.Name;
        return binding;
    }
    private async Task<LanguageServerSession> StartAsync(LanguageServerConfiguration configuration, string root)
    {
        var currentGeneration = generation;
        var server = await LanguageServerSession.StartAsync(configuration, root, lifetime.Token);
        server.Diagnostics += report => Dispatcher.UIThread.Post(() => ReceiveDiagnostics(server, report));
        server.Status += status => Dispatcher.UIThread.Post(() => { if (!disposed && currentGeneration == generation) window.State.CodeStatus = status; });
        return server;
    }
    private void DocumentChanged(object? sender, EventArgs arguments)
    {
        if (disposed || restarting) return;
        foreach (var pending in documents.Values.Where(task => task.IsCompletedSuccessfully))
            if (pending.Result is { } binding && binding.Tab.Document == sender) binding.Diagnostics = [];
        RefreshDiagnostics();
        request?.Cancel();
        debounce.Stop(); debounce.Start();
    }
    private async Task<Binding?> FlushAsync(EditorTab tab)
    {
        var binding = await GetBindingAsync(tab);
        if (binding is null) return null;
        await binding.Changes.WaitAsync(lifetime.Token);
        try
        {
            var text = tab.Document.Text;
            if (text != binding.Text)
            {
                var previous = binding.Text;
                binding.Text = text;
                binding.Version++;
                binding.Diagnostics = [];
                await binding.Session.ChangeAsync(binding.Path, previous, text, binding.Version, lifetime.Token);
            }
            return binding;
        }
        finally { binding.Changes.Release(); }
    }
    public async Task SynchronizeAllAsync()
    {
        foreach (var tab in window.State.Tabs.OfType<EditorTab>().ToArray())
            try { await FlushAsync(tab); }
            catch (Exception exception) when (Expected(exception)) { Report(exception); }
    }
    public async Task SavedAsync(EditorTab tab)
    {
        try
        {
            if (documents.TryGetValue(tab, out var pending) && await pending is { } old && old.Path != tab.FilePath)
            {
                await old.Session.CloseAsync(old.Path, lifetime.Token);
                documents.Remove(tab);
            }
            // Untitled buffers acquire a server only after they have a file path.
            if (documents.TryGetValue(tab, out pending) && pending.IsCompletedSuccessfully && pending.Result is null) documents.Remove(tab);
            if (await FlushAsync(tab) is { } binding) await binding.Session.SaveAsync(binding.Path, binding.Text, lifetime.Token);
        }
        catch (Exception exception) when (Expected(exception)) { Report(exception); }
    }
    private void ReceiveDiagnostics(LanguageServerSession server, PublishedDiagnostics report)
    {
        if (disposed || restarting || !Uri.TryCreate(report.Uri, UriKind.Absolute, out var uri) || !uri.IsFile) return;
        foreach (var pending in documents.Values.Where(task => task.IsCompletedSuccessfully))
        {
            if (pending.Result is not { } binding || binding.Session != server || !ProjectFiles.PathComparer.Equals(binding.Path, uri.LocalPath)) continue;
            if (report.Version is int version && version != binding.Version || binding.Text != binding.Tab.Document.Text) return;
            binding.Diagnostics = report.Diagnostics;
        }
        RefreshDiagnostics();
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs arguments)
    {
        if (arguments.PropertyName != nameof(WorkspaceState.ActiveTab)) return;
        completionWindow?.Close();
        autoComplete.Stop();
        ToolTip.SetIsOpen(editor, false);
        request?.Cancel();
        RefreshDiagnostics();
    }
    public void RefreshDiagnostics()
    {
        if (disposed) return;
        var bindings = documents.Values.Where(task => task.IsCompletedSuccessfully && task.Result is not null).Select(task => task.Result!).ToArray();
        var items = bindings.SelectMany(binding => binding.Diagnostics.Select(diagnostic => new DiagnosticItem(binding.Path, diagnostic))).ToArray();
        if (!Diagnostics.Items.SequenceEqual(items))
        {
            Diagnostics.Items.Clear();
            foreach (var item in items) Diagnostics.Items.Add(item);
        }
        window.State.DiagnosticStatus = $"{items.Count(item => item.Diagnostic.Severity == 1)} errors · {items.Count(item => item.Diagnostic.Severity == 2)} warnings";
        renderer.SetDiagnostics(window.State.ActiveDocument?.Document, bindings.FirstOrDefault(binding => binding.Tab == window.State.ActiveDocument)?.Diagnostics ?? []);
        renderer.Theme = (Application.Current as App)?.EditorTheme;
        editor.TextArea.TextView.InvalidateLayer(renderer.Layer);
    }
    public Task ShowDiagnosticsAsync()
    {
        if (!window.State.Tabs.Contains(Diagnostics)) window.State.Tabs.Add(Diagnostics);
        window.State.ActiveTab = Diagnostics;
        return Task.CompletedTask;
    }
    private CancellationToken BeginRequest()
    {
        request?.Cancel();
        request?.Dispose();
        request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(15));
        return request.Token;
    }
    public async Task ShowCompletionsAsync()
    {
        var tab = window.State.ActiveDocument;
        if (tab is null) return;
        var token = BeginRequest();
        try
        {
            var binding = await FlushAsync(tab);
            if (binding is null) { window.State.Status = window.State.CodeStatus; return; }
            if (!binding.Session.Supports("completionProvider")) return;
            var snapshot = tab.Document.Text;
            var offset = editor.CaretOffset;
            var result = await binding.Session.RequestAtAsync("textDocument/completion", binding.Path, LspText.Position(snapshot, offset), token);
            if (disposed || window.State.ActiveDocument != tab || tab.Document.Text != snapshot || editor.CaretOffset != offset) return;
            var items = LanguageServerSession.Completions(result);
            LastCompletions = items;
            completionWindow?.Close();
            if (items.Count == 0) { window.State.Status = "No completions at this position"; return; }
            var start = offset;
            while (start > 0 && (char.IsLetterOrDigit(snapshot[start - 1]) || snapshot[start - 1] == '_')) start--;
            var popup = new CompletionWindow(editor.TextArea) { StartOffset = start, EndOffset = offset, CloseWhenCaretAtBeginning = false, MaxHeight = 320, MinWidth = 260 };
            completionWindow = popup;
            foreach (var item in items.Take(500)) popup.CompletionList.CompletionData.Add(new CompletionItem(item, (area, segment) => ApplyCompletion(tab, snapshot, start, offset, item, area, segment)));
            popup.Closed += (_, _) => { if (completionWindow == popup) completionWindow = null; };
            var theme = (Application.Current as App)?.EditorTheme;
            if (theme is not null)
            {
                popup.CompletionList.Background = theme.Brush("elevated_surface.background");
                popup.CompletionList.Foreground = theme.Brush("text");
                popup.CompletionList.BorderBrush = theme.Brush("border");
                popup.CompletionList.BorderThickness = new Thickness(1);
                popup.CompletionList.FontFamily = new FontFamily("avares://Zyber/Assets#IBM Plex Sans");
                popup.CompletionList.FontSize = 14;
            }
            popup.Show();
            if (theme is not null && popup.CompletionList.ListBox is { } list)
            {
                list.Background = theme.Brush("elevated_surface.background");
                list.Foreground = theme.Brush("text");
            }
            popup.CompletionList.SelectItem(snapshot[start..offset]);
            if (items.Count > 500) window.State.Status = "Showing the first 500 completions; type a prefix and press Ctrl+Space to query again.";
        }
        catch (Exception exception) when (Expected(exception)) { Report(exception); }
    }
    private void ApplyCompletion(EditorTab tab, string snapshot, int start, int offset, LspCompletion item, TextArea area, ISegment segment)
    {
        try
        {
            if (window.State.ActiveDocument != tab || area.Document != tab.Document) return;
            var current = tab.Document.Text;
            if (segment.Offset != start || !current[..start].Equals(snapshot[..start], StringComparison.Ordinal) || !current[segment.EndOffset..].Equals(snapshot[offset..], StringComparison.Ordinal))
                throw new IOException("The document changed outside the completion range. Request completion again.");
            var edit = item.Edit ?? new LspTextEdit(new(LspText.Position(snapshot, start), LspText.Position(snapshot, offset)), item.InsertText);
            var edits = LspText.ValidateEdits(snapshot, item.AdditionalEdits.Append(edit));
            var delta = segment.Length - (offset - start);
            int Map(int value)
            {
                if (value <= start) return value;
                if (value >= offset) return value + delta;
                throw new IOException("The completion range no longer matches the document.");
            }
            var primaryOffset = LspText.Offset(snapshot, edit.Range.Start);
            var primaryEnd = LspText.Offset(snapshot, edit.Range.End);
            var mapped = edits.Select(change =>
            {
                var end = change.Offset + change.Length;
                var isPrimary = change.Offset == primaryOffset && end == primaryEnd && change.Text == edit.NewText;
                return (Offset: Map(change.Offset), End: isPrimary && end == offset ? segment.EndOffset : Map(end), change.Text);
            }).ToArray();
            var mainStart = Map(LspText.Offset(snapshot, edit.Range.Start));
            var caret = mainStart + edit.NewText.Length + mapped.Where(change => change.Offset < mainStart).Sum(change => change.Text.Length - (change.End - change.Offset));
            using (tab.Document.RunUpdate())
                foreach (var change in mapped) tab.Document.Replace(change.Offset, change.End - change.Offset, change.Text);
            editor.CaretOffset = Math.Clamp(caret, 0, tab.Document.TextLength);
        }
        catch (IOException exception) { window.State.Status = exception.Message; }
    }
    public async Task ShowHoverAsync()
    {
        var tab = window.State.ActiveDocument;
        if (tab is null) return;
        var token = BeginRequest();
        try
        {
            var binding = await FlushAsync(tab);
            if (binding is null) { window.State.Status = window.State.CodeStatus; return; }
            if (!binding.Session.Supports("hoverProvider")) return;
            var snapshot = tab.Document.Text;
            var offset = editor.CaretOffset;
            var hover = await binding.Session.RequestAtAsync("textDocument/hover", binding.Path, LspText.Position(snapshot, offset), token);
            if (window.State.ActiveDocument != tab || tab.Document.Text != snapshot || editor.CaretOffset != offset || hover.ValueKind == System.Text.Json.JsonValueKind.Null) return;
            var text = LspText.Documentation(hover.GetProperty("contents"));
            if (string.IsNullOrWhiteSpace(text)) return;
            ToolTip.SetTip(editor, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, FontSize = 14 });
            ToolTip.SetIsOpen(editor, true);
        }
        catch (Exception exception) when (Expected(exception)) { Report(exception); }
    }
    public async Task<OutlineSnapshot> DocumentSymbolsAsync(EditorTab tab, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        if (Path.GetExtension(tab.FilePath)?.ToLowerInvariant() is ".json" or ".jsonc")
        {
            var text = tab.Document.Text;
            return await Task.Run(() => DocumentOutline.Json(text, linked.Token), linked.Token);
        }
        var binding = await FlushAsync(tab);
        if (binding is null || !binding.Session.Supports("documentSymbolProvider"))
            return new(tab.Document.Text, [], "No document symbols available from a language server for this file.");
        var snapshot = binding.Text;
        var result = await binding.Session.DocumentSymbolsAsync(binding.Path, linked.Token);
        return DocumentOutline.FromLanguageServer(snapshot, binding.Path, result);
    }

    public async Task GoToDefinitionAsync(bool references = false)
    {
        var tab = window.State.ActiveDocument;
        if (tab is null) return;
        var token = BeginRequest();
        try
        {
            var binding = await FlushAsync(tab);
            if (binding is null) { window.State.Status = window.State.CodeStatus; return; }
            if (!binding.Session.Supports(references ? "referencesProvider" : "definitionProvider")) return;
            var snapshot = tab.Document.Text;
            var position = LspText.Position(snapshot, editor.CaretOffset);
            var result = references ? await binding.Session.ReferencesAsync(binding.Path, position, token)
                : await binding.Session.RequestAtAsync("textDocument/definition", binding.Path, position, token);
            if (window.State.ActiveDocument != tab || tab.Document.Text != snapshot) return;
            var locations = LanguageServerSession.Locations(result);
            if (locations.Count == 0) { window.State.Status = references ? "No references found" : "No definition found"; return; }
            if (locations.Count == 1) await OpenLocationAsync(locations[0]);
            else
            {
                var commands = locations.Select(location => new EditorCommand(location.Label, new Uri(location.Uri).LocalPath, () => OpenLocationAsync(location))).ToArray();
                var selected = await Dialogs.ChooseCommandAsync(window, commands);
                if (selected is not null) await selected.Execute();
            }
        }
        catch (Exception exception) when (Expected(exception)) { Report(exception); }
    }
    public async Task OpenLocationAsync(LspLocation location)
    {
        var uri = new Uri(location.Uri);
        if (!uri.IsFile) throw new IOException("Only local language-server locations are supported.");
        await window.OpenDocumentAsync(uri.LocalPath);
        var text = window.State.ActiveDocument!.Document.Text;
        var start = LspText.Offset(text, location.Range.Start);
        var end = LspText.Offset(text, location.Range.End);
        editor.Select(start, Math.Max(0, end - start));
        editor.ScrollTo(editor.TextArea.Caret.Line, editor.TextArea.Caret.Column);
        editor.TextArea.Focus();
    }
    public async Task FormatAsync()
    {
        var tab = window.State.ActiveDocument;
        if (tab is null) return;
        var token = BeginRequest();
        try
        {
            var binding = await FlushAsync(tab);
            if (binding is null || !binding.Session.Supports("documentFormattingProvider")) { window.State.Status = "This language server does not provide formatting"; return; }
            var snapshot = tab.Document.Text;
            var result = await binding.Session.FormatAsync(binding.Path, editor.Options.IndentationSize, editor.Options.ConvertTabsToSpaces, token);
            if (tab.Document.Text != snapshot) { window.State.Status = "Formatting discarded because the document changed"; return; }
            if (result.ValueKind == System.Text.Json.JsonValueKind.Null) return;
            var edits = LspText.ValidateEdits(snapshot, result.EnumerateArray().Select(LspText.Edit));
            using (tab.Document.RunUpdate())
                foreach (var edit in edits) tab.Document.Replace(edit.Offset, edit.Length, edit.Text);
        }
        catch (Exception exception) when (Expected(exception)) { Report(exception); }
    }
    public async Task RestartAsync()
    {
        if (disposed || restarting) return;
        restarting = true;
        generation++;
        debounce.Stop(); autoComplete.Stop();
        request?.Cancel(); completionWindow?.Close();
        await lifetime.CancelAsync();
        foreach (var startup in sessions.Values.ToArray())
            try { await (await startup).DisposeAsync(); }
            catch (Exception exception) when (Expected(exception)) { Report(exception); }
        sessions.Clear(); documents.Clear();
        if (disposed) return;
        lifetime = new CancellationTokenSource();
        restarting = false;
        foreach (var tab in subscribed.Where(tab => !window.State.Tabs.Contains(tab)).ToArray())
        {
            tab.Document.TextChanged -= DocumentChanged;
            subscribed.Remove(tab);
        }
        foreach (var tab in window.State.Tabs.OfType<EditorTab>().ToArray())
        {
            if (subscribed.Add(tab)) tab.Document.TextChanged += DocumentChanged;
            await ConnectSafelyAsync(tab);
        }
        RefreshDiagnostics();
    }

    private static bool Expected(Exception exception) => exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException;
    private void Report(Exception exception)
    {
        if (!disposed && exception is not OperationCanceledException) window.State.CodeStatus = exception.Message;
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        debounce.Stop(); autoComplete.Stop();
        request?.Cancel();
        await lifetime.CancelAsync();
        completionWindow?.Close();
        ToolTip.SetIsOpen(editor, false);
        window.State.Tabs.CollectionChanged -= TabsChanged;
        window.State.PropertyChanged -= StateChanged;
        foreach (var tab in subscribed) tab.Document.TextChanged -= DocumentChanged;
        foreach (var startup in sessions.Values.ToArray())
            try { await (await startup).DisposeAsync(); }
            catch (Exception exception) when (Expected(exception)) { }
        editor.TextArea.TextView.BackgroundRenderers.Remove(renderer);
    }

    private sealed class CompletionItem(LspCompletion item, Action<TextArea, ISegment> complete) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => item.Label;
        public object Content => item.Label;
        public object? Description
        {
            get
            {
                var text = string.Join("\n\n", new[] { item.Detail, item.Documentation }.Where(value => !string.IsNullOrWhiteSpace(value)));
                return text.Length == 0 ? null : new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 };
            }
        }
        public double Priority => 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => complete(textArea, completionSegment);
    }
}

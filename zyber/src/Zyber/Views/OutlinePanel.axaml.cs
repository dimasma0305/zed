using System.ComponentModel;
using System.Collections.Specialized;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Zyber.Models;
using Zyber.Services;

namespace Zyber.Views;

public sealed class OutlineNode(OutlineSymbol symbol, IReadOnlyList<OutlineNode> children, string identity) : ObservableState
{
    public OutlineSymbol Symbol { get; } = symbol;
    public string Name => Symbol.Name;
    public string Identity { get; } = identity;
    public IReadOnlyList<OutlineNode> Children { get; } = children;
    private bool expanded = true;
    public bool IsExpanded { get => expanded; set { expanded = value; Notify(); } }
    public IBrush Foreground => Application.Current is App application
        ? new SolidColorBrush(application.EditorTheme.SyntaxColor(Symbol.Kind switch { 5 or 10 or 11 or 23 => "type", 6 or 9 or 12 => "function", 7 or 8 => "property", 14 => "constant", _ => "primary" })) : Brushes.Gray;
    public void RefreshTheme() { Notify(nameof(Foreground)); foreach (var child in Children) child.RefreshTheme(); }
}

public sealed partial class OutlinePanel : UserControl, IDisposable
{
    private MainWindow? window;
    private TextEditor? editor;
    private EditorTab? document;
    private OutlineSnapshot? snapshot;
    private ITextSourceVersion? snapshotVersion;
    private readonly Dictionary<string, bool> expanded = [];
    private readonly Dictionary<OutlineSymbol, OutlineNode> visibleNodes = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<OutlineNode> nodes = [];
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? pending;
    private int generation;
    private bool followingCaret;
    private string lastQuery = "";
    private bool disposed;
    public bool IsPinned { get; private set; }
    public IReadOnlyList<OutlineNode> Nodes => nodes;

    public OutlinePanel()
    {
        InitializeComponent();
        debounce.Tick += async (_, _) => { debounce.Stop(); await RefreshAsync(); };
    }

    public void Attach(MainWindow owner, TextEditor textEditor)
    {
        window = owner;
        editor = textEditor;
        owner.State.PropertyChanged += StateChanged;
        owner.State.Tabs.CollectionChanged += TabsChanged;
        editor.TextArea.Caret.PositionChanged += CaretChanged;
        if (Application.Current is App application) application.EditorThemeChanged += ThemeChanged;
        BindDocument();
    }

    private void StateChanged(object? sender, PropertyChangedEventArgs arguments)
    {
        if (arguments.PropertyName == nameof(WorkspaceState.ActiveTab)) BindDocument();
        else if (arguments.PropertyName == nameof(WorkspaceState.ShowOutlinePanel)) QueueRefresh();
    }
    public void TogglePin()
    {
        if (document is null) return;
        IsPinned = !IsPinned;
        UpdatePin();
        if (!IsPinned) BindDocument();
    }
    private void PinClicked(object? sender, RoutedEventArgs arguments) => TogglePin();
    private void UpdatePin()
    {
        PinIcon.Kind = IsPinned ? "unpin" : "pin";
        ToolTip.SetTip(PinButton, IsPinned ? "Unpin " + document?.Name : "Pin this buffer");
    }
    private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs arguments)
    {
        if (IsPinned && document is not null && window?.State.Tabs.Contains(document) != true)
        {
            IsPinned = false;
            UpdatePin();
            BindDocument();
        }
    }
    private void BindDocument()
    {
        if (IsPinned && document is not null) { FollowCaret(); return; }
        if (document is not null) { document.Document.TextChanged -= DocumentChanged; document.PropertyChanged -= DocumentDetailsChanged; }
        document = window?.State.ActiveDocument;
        if (document is not null && window?.State.Tabs.Contains(document) != true) document = null;
        expanded.Clear();
        visibleNodes.Clear();
        snapshot = null;
        snapshotVersion = null;
        lastQuery = "";
        nodes = [];
        if (document is not null) { document.Document.TextChanged += DocumentChanged; document.PropertyChanged += DocumentDetailsChanged; }
        Query.Text = "";
        QueueRefresh();
    }
    private void DocumentDetailsChanged(object? sender, PropertyChangedEventArgs arguments)
    {
        if (arguments.PropertyName == nameof(EditorTab.FilePath)) QueueRefresh();
    }
    private void DocumentChanged(object? sender, EventArgs arguments) => QueueRefresh();
    private void QueueRefresh()
    {
        RememberExpansion();
        generation++;
        pending?.Cancel();
        snapshot = null;
        snapshotVersion = null;
        visibleNodes.Clear();
        nodes = [];
        Symbols.ItemsSource = nodes;
        if (window is not null) window.State.CurrentSymbol = "";
        debounce.Stop();
        if (!disposed && window?.State.ShowOutlinePanel == true) debounce.Start();
    }
    public async Task RefreshAsync()
    {
        debounce.Stop();
        pending?.Cancel();
        pending?.Dispose();
        var request = pending = new CancellationTokenSource();
        var currentGeneration = ++generation;
        var tab = document;
        if (disposed || window is null || !window.State.ShowOutlinePanel) return;
        if (tab is null) { SetMessage("Open a file to view its symbols"); return; }
        SetMessage("Loading symbols…");
        try
        {
            var result = await window.Code.DocumentSymbolsAsync(tab, request.Token);
            if (disposed || request.IsCancellationRequested || currentGeneration != generation || document != tab || tab.Document.Text != result.Text) return;
            snapshot = result;
            snapshotVersion = tab.Document.Version;
            Filter();
        }
        catch (OperationCanceledException) { if (!disposed && currentGeneration == generation) SetMessage("Symbol request cancelled or timed out"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        { if (!disposed && currentGeneration == generation) SetMessage("Outline: " + exception.Message); }
    }
    private void QueryChanged(object? sender, TextChangedEventArgs arguments) { if (Symbols is not null) Filter(); }
    private void RememberExpansion()
    {
        if (lastQuery.Length == 0)
            foreach (var node in visibleNodes.Values) expanded[node.Identity] = node.IsExpanded;
    }
    private void Filter()
    {
        RememberExpansion();
        visibleNodes.Clear();
        var query = Query.Text?.Trim() ?? "";
        lastQuery = query;
        IReadOnlyList<OutlineNode> Match(IEnumerable<OutlineSymbol> symbols, bool parentMatches, string parent)
        {
            List<OutlineNode> filtered = [];
            var index = 0;
            foreach (var symbol in symbols)
            {
                var identity = parent + "/" + index++ + ":" + symbol.Name.Length + ":" + symbol.Name;
                var matches = parentMatches || symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                var children = Match(symbol.Children, matches, identity);
                if (matches || children.Count > 0)
                {
                    var node = new OutlineNode(symbol, children, identity) { IsExpanded = query.Length > 0 || !expanded.TryGetValue(identity, out var open) || open };
                    filtered.Add(node);
                    visibleNodes[symbol] = node;
                }
            }
            return filtered;
        }
        nodes = Match(snapshot?.Symbols ?? [], false, "");
        followingCaret = true;
        try { Symbols.ItemsSource = nodes; }
        finally { followingCaret = false; }
        SetMessage(snapshot?.Message ?? (nodes.Count == 0 ? "No matching symbols" : ""));
        FollowCaret();
    }
    public void FocusQuery() => Query.Focus();
    private void CaretChanged(object? sender, EventArgs arguments) => FollowCaret();
    private void FollowCaret()
    {
        if (editor is null || window is null || snapshot is null || document is null || snapshotVersion?.CompareAge(document.Document.Version) != 0) return;
        if (window.State.ActiveDocument != document || editor.Document != document.Document)
        {
            followingCaret = true;
            try { Symbols.SelectedItem = null; }
            finally { followingCaret = false; }
            window.State.CurrentSymbol = "";
            return;
        }
        OutlineSymbol? selected = null;
        List<string> names = [];
        void Find(IReadOnlyList<OutlineSymbol> entries, List<string> ancestors)
        {
            foreach (var symbol in entries)
                if (editor.CaretOffset >= symbol.Start && editor.CaretOffset < symbol.End)
                {
                    selected = symbol; names = [.. ancestors, symbol.Name];
                    Find(symbol.Children, names);
                }
        }
        Find(snapshot.Symbols, []);
        followingCaret = true;
        try { Symbols.SelectedItem = selected is not null && visibleNodes.TryGetValue(selected, out var node) ? node : null; }
        finally { followingCaret = false; }
        window.State.CurrentSymbol = string.Join(" › ", names);
    }
    public void Navigate(OutlineNode node, bool focusEditor = false)
    {
        if (editor is null || snapshot is null || document is null || window is null || snapshotVersion?.CompareAge(document.Document.Version) != 0 || !visibleNodes.TryGetValue(node.Symbol, out var current) || !ReferenceEquals(node, current)) return;
        if (window.State.ActiveDocument != document) window.State.ActiveTab = document;
        editor.Select(node.Symbol.SelectionStart, node.Symbol.SelectionEnd - node.Symbol.SelectionStart);
        editor.ScrollToLine(editor.Document.GetLineByOffset(node.Symbol.SelectionStart).LineNumber);
        if (focusEditor) editor.TextArea.Focus();
    }
    private void SymbolSelected(object? sender, SelectionChangedEventArgs arguments)
    {
        if (!followingCaret && Symbols.SelectedItem is OutlineNode node) Navigate(node);
    }
    private void SymbolKeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key == Key.Enter && Symbols.SelectedItem is OutlineNode node) { Navigate(node, true); arguments.Handled = true; }
        else if (arguments.Key == Key.Escape) { editor?.TextArea.Focus(); arguments.Handled = true; }
    }
    private void SetMessage(string message) { Message.Text = message; Message.IsVisible = message.Length > 0; }
    private void ThemeChanged(object? sender, EventArgs arguments) { foreach (var node in nodes) node.RefreshTheme(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        debounce.Stop(); pending?.Cancel(); pending?.Dispose();
        if (document is not null) { document.Document.TextChanged -= DocumentChanged; document.PropertyChanged -= DocumentDetailsChanged; }
        if (window is not null) { window.State.PropertyChanged -= StateChanged; window.State.Tabs.CollectionChanged -= TabsChanged; }
        if (editor is not null) editor.TextArea.Caret.PositionChanged -= CaretChanged;
        if (Application.Current is App application) application.EditorThemeChanged -= ThemeChanged;
    }
}

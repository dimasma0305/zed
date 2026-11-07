using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Zyber.Models;
using Zyber.Services;

namespace Zyber.Views;

public sealed record GitDiffRequest(string Title, string Text);

public sealed partial class GitPanel : UserControl, IDisposable
{
    public GitPanelState State { get; } = new();
    public event EventHandler<GitDiffRequest>? DiffRequested;
    public event EventHandler? RepositoryChanged;
    private GitRepository? repository;
    private FileSystemWatcher? metadataWatcher;
    private CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer refreshDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool refreshing;
    private bool refreshAgain;
    private bool disposed;
    private string? lastError;
    private int generation;

    public GitPanel()
    {
        InitializeComponent();
        DataContext = State;
        refreshDebounce.Tick += async (_, _) => { refreshDebounce.Stop(); await RefreshAsync(); };
    }
    public async Task SetProjectAsync(string path)
    {
        var current = ++generation;
        lifetime.Cancel(); lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        metadataWatcher?.Dispose(); metadataWatcher = null;
        repository = null;
        lastError = null;
        State.HasRepository = false;
        State.Changes.Clear(); State.History.Clear(); State.CommitMessage = ""; State.Branch = "";
        State.Message = "Reading repository…";
        State.Changed();
        try
        {
            var opened = await GitRepository.OpenAsync(path, token);
            if (disposed || current != generation) return;
            repository = opened;
            State.HasRepository = opened is not null;
            if (opened is null) { State.Message = "This project is not in a Git repository"; return; }
            State.Message = "";
            metadataWatcher = new FileSystemWatcher(opened.MetadataDirectory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            void Changed(object sender, FileSystemEventArgs arguments)
            {
                var name = Path.GetRelativePath(opened.MetadataDirectory, arguments.FullPath);
                if (name is "index" or "HEAD" or "packed-refs" or "config" || name.StartsWith("refs" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) QueueRefresh();
            }
            metadataWatcher.Changed += Changed;
            metadataWatcher.Created += Changed;
            metadataWatcher.Deleted += Changed;
            metadataWatcher.Renamed += (sender, arguments) => Changed(sender, arguments);
            metadataWatcher.Error += (_, _) => QueueRefresh();
            metadataWatcher.EnableRaisingEvents = true;
            await RefreshAsync();
        }
        catch (Exception exception) when (Expected(exception)) { if (!disposed && current == generation) State.Message = exception.Message; }
    }
    public void QueueRefresh() => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        refreshDebounce.Stop(); refreshDebounce.Start();
    });
    public async Task RefreshAsync()
    {
        if (disposed || repository is null) return;
        if (refreshing || State.Busy) { refreshAgain = true; return; }
        var current = generation;
        var target = repository;
        var token = lifetime.Token;
        refreshing = true;
        State.Busy = true;
        try
        {
            var snapshot = await target.StatusAsync(token);
            var history = await target.HistoryAsync(token);
            if (disposed || current != generation) return;
            State.Changes.Clear();
            foreach (var change in snapshot.Changes.Where(change => change.HasStaged)) State.Changes.Add(new(change, true));
            foreach (var change in snapshot.Changes.Where(change => change.HasUnstaged)) State.Changes.Add(new(change, false));
            State.History.Clear();
            foreach (var commit in history) State.History.Add(commit);
            State.Branch = snapshot.Branch;
            State.Sync = snapshot.Ahead > 0 ? $"↑{snapshot.Ahead} Push" : "Push";
            State.Message = lastError ?? (snapshot.Behind > 0 ? $"{snapshot.Behind} commits behind {snapshot.Upstream}" : "");
            State.Changed();
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (Expected(exception)) { if (!disposed && current == generation) State.Message = exception.Message; }
        finally
        {
            refreshing = false;
            State.Busy = false;
            if (refreshAgain) { refreshAgain = false; QueueRefresh(); }
        }
    }
    private async Task RunAsync(Func<GitRepository, CancellationToken, Task> operation)
    {
        if (disposed || State.Busy || repository is null) return;
        State.Busy = true;
        var current = generation;
        lastError = null;
        string? error = null;
        try { await operation(repository, lifetime.Token); }
        catch (Exception exception) when (Expected(exception)) { error = exception.Message; }
        finally { State.Busy = false; }
        if (disposed || current != generation) return;
        lastError = error;
        await RefreshAsync();
        if (error is not null) State.Message = error;
    }
    public Task ShowChangeAsync(GitChangeItem item) => RunAsync(async (target, token) =>
    {
        var text = await target.DiffAsync(item.Change, item.Staged, token);
        DiffRequested?.Invoke(this, new(item.Path + (item.Staged ? " (staged)" : " (working tree)"), text));
    });
    public Task StageAsync(GitChangeItem item) => RunAsync((target, token) => item.Staged ? target.UnstageAsync(item.Change, token) : target.StageAsync(item.Change, token));
    public Task CommitAsync() => RunAsync(async (target, token) =>
    {
        await target.CommitAsync(State.CommitMessage, !State.Changes.Any(change => change.Staged), token);
        State.CommitMessage = "";
    });
    private void ChangesClicked(object? sender, RoutedEventArgs arguments) => State.ShowHistory = false;
    private void HistoryClicked(object? sender, RoutedEventArgs arguments) => State.ShowHistory = true;
    private async void StageClicked(object? sender, RoutedEventArgs arguments)
    {
        arguments.Handled = true;
        if (sender is Button { DataContext: GitChangeItem item }) await StageAsync(item);
    }
    private async void StageAllClicked(object? sender, RoutedEventArgs arguments) => await RunAsync((target, token) => target.StageAllAsync(token));
    private async void CommitClicked(object? sender, RoutedEventArgs arguments) => await CommitAsync();
    private async void PushClicked(object? sender, RoutedEventArgs arguments) => await RunAsync((target, token) => target.PushAsync(token));
    private async void FetchClicked(object? sender, RoutedEventArgs arguments) => await RunAsync((target, token) => target.FetchAsync(token));
    private async void ViewDiffClicked(object? sender, RoutedEventArgs arguments) => await RunAsync(async (target, token) =>
    {
        var text = await target.DiffAsync(cancellationToken: token);
        var staged = await target.DiffAsync(staged: true, cancellationToken: token);
        var combined = new System.Text.StringBuilder((staged.Length > 0 ? "Staged changes\n\n" + staged + "\n" : "") + (text.Length > 0 ? "Working tree changes\n\n" + text : ""));
        if (combined.Length > 8 * 1024 * 1024) throw new IOException("Project diff is too large to display together. Open individual file diffs.");
        foreach (var item in State.Changes.Where(item => item.Change.Untracked).ToArray())
        {
            combined.Append("\nUntracked file\n\n").Append(await target.DiffAsync(item.Change, cancellationToken: token));
            if (combined.Length > 8 * 1024 * 1024) throw new IOException("Project diff is too large to display together. Open individual file diffs.");
        }
        DiffRequested?.Invoke(this, new("Project Diff", combined.ToString()));
    });
    private async void ChangeDoubleTapped(object? sender, TappedEventArgs arguments) { if (Changes.SelectedItem is GitChangeItem item) await ShowChangeAsync(item); }
    private async void ChangeKeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key == Key.Enter && Changes.SelectedItem is GitChangeItem item) { arguments.Handled = true; await ShowChangeAsync(item); }
    }
    private async Task ShowCommitAsync(GitCommit commit) => await RunAsync(async (target, token) => DiffRequested?.Invoke(this, new(commit.ShortHash + " " + commit.Subject, await target.CommitDiffAsync(commit.Hash, token))));
    private async void HistoryDoubleTapped(object? sender, TappedEventArgs arguments) { if (History.SelectedItem is GitCommit commit) await ShowCommitAsync(commit); }
    private async void HistoryKeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key == Key.Enter && History.SelectedItem is GitCommit commit) { arguments.Handled = true; await ShowCommitAsync(commit); }
    }
    private static bool Expected(Exception exception) => exception is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception;
    public void Dispose()
    {
        disposed = true;
        lifetime.Cancel();
        metadataWatcher?.Dispose();
        refreshDebounce.Stop();
    }
}

using Zyber.Services;

namespace Zyber.Models;

public abstract class WorkspaceTab : ObservableState
{
    public abstract string Name { get; }
    public abstract string Caption { get; }
    public virtual bool IsDirty => false;
}

public sealed class SearchTab(string root) : WorkspaceTab, IDisposable
{
    public string Root { get; } = root;
    public override string Name => "Search";
    public override string Caption => string.IsNullOrEmpty(Query) ? "Search" : "Search: " + Query;
    private string query = "";
    public string Query { get => query; set { query = value; Notify(); Notify(nameof(Caption)); } }
    public bool CaseSensitive { get; set; }
    public bool WholeWord { get; set; }
    public bool RegularExpression { get; set; }
    private IReadOnlyList<SearchHit> results = [];
    public IReadOnlyList<SearchHit> Results { get => results; private set { results = value; Notify(); } }
    private string status = "Search project files";
    public string Status { get => status; private set { status = value; Notify(); } }
    private string details = "";
    public string Details { get => details; private set { details = value; Notify(); } }
    private bool running;
    public bool IsRunning { get => running; private set { running = value; Notify(); } }
    private CancellationTokenSource? currentSearch;
    private bool disposed;

    public async Task SearchAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        currentSearch?.Cancel();
        var request = new CancellationTokenSource();
        currentSearch = request;
        var token = request.Token;
        var options = new SearchOptions(Query, CaseSensitive, WholeWord, RegularExpression);
        Results = [];
        Details = "";
        IsRunning = true;
        Status = "Searching…";
        try
        {
            var index = await ProjectFiles.IndexAsync(Root, token);
            var report = await Task.Run(() => ProjectSearch.FindAsync(index, options, token), token);
            if (currentSearch != request || disposed) return;
            Results = report.Hits;
            Status = $"{report.Hits.Count} matches in {report.Hits.Select(hit => hit.FullPath).Distinct(ProjectFiles.PathComparer).Count()} files · {report.FilesSearched} files searched";
            if (report.LimitReached) Status += " · limit reached; narrow your search";
            if (report.FilesSkipped > 0) Status += $" · {report.FilesSkipped} files skipped";
            if (report.Warnings.Count > 0) Status += " · see details";
            Details = string.Join(Environment.NewLine, report.Warnings);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (currentSearch == request && !disposed) Status = "Search cancelled";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (currentSearch == request && !disposed) Status = exception.Message;
        }
        finally
        {
            if (currentSearch == request) { currentSearch = null; IsRunning = false; }
            request.Dispose();
        }
    }

    public void Cancel() => currentSearch?.Cancel();
    public void Dispose() { disposed = true; Cancel(); }
}

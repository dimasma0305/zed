namespace Zyber.Services;

public sealed class WorkspaceWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    public event EventHandler? Changed;
    public event EventHandler<string>? Failed;

    public void Watch(string? project, IEnumerable<string> files)
    {
        Dispose();
        var directories = new Dictionary<string, bool>(ProjectFiles.PathComparer);
        if (project is not null) directories[project] = true;
        foreach (var path in files)
        {
            var parent = Path.GetDirectoryName(path)!;
            var relative = project is null ? null : Path.GetRelativePath(project, parent);
            if (relative is not null && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            directories.TryAdd(parent, false);
        }
        foreach (var (directory, recursive) in directories)
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Deleted += OnChange;
                watcher.Renamed += OnChange;
                watcher.Error += (_, arguments) => { Failed?.Invoke(this, arguments.GetException().Message); Changed?.Invoke(this, EventArgs.Empty); };
                watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Failed?.Invoke(this, exception.Message);
            }
        }
    }
    private void OnChange(object sender, FileSystemEventArgs arguments)
    {
        if (arguments.FullPath.Split(Path.DirectorySeparatorChar).Contains(".git")) return;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
    }
}

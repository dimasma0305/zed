using System.Text.Json;

namespace Zyber.Services;

public sealed class RecentProjects(string? storagePath)
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly List<string> memory = [];

    public static string DefaultStoragePath => Path.Combine(
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : Environment.GetEnvironmentVariable("XDG_STATE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
        "Zyber", "recent-projects.json");

    public async Task<IReadOnlyList<string>> ReadAsync()
    {
        if (storagePath is null) return memory.ToArray();
        if (!File.Exists(storagePath)) return [];
        await using var stream = File.OpenRead(storagePath);
        if (stream.Length > 1024 * 1024) throw new IOException("Recent projects file is too large.");
        try { return await JsonSerializer.DeserializeAsync<string[]>(stream) ?? []; }
        catch (JsonException exception) { throw new IOException("Recent projects file is damaged. Existing data has been preserved.", exception); }
    }

    public async Task AddAsync(string path)
    {
        await writes.WaitAsync();
        try
        {
            var projects = (await ReadAsync()).Prepend(Path.GetFullPath(path)).Distinct(ProjectFiles.PathComparer).Take(30).ToArray();
            if (storagePath is null) { memory.Clear(); memory.AddRange(projects); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
            var temporary = storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(projects));
                File.Move(temporary, storagePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { writes.Release(); }
    }
}

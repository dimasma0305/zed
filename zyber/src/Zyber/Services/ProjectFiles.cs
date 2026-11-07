using System.Diagnostics;
using System.Text;

namespace Zyber.Services;

public sealed record ProjectFile(string FullPath, string RelativePath);
public sealed record ProjectFileIndex(string Root, IReadOnlyList<ProjectFile> Files, IReadOnlyList<string> Warnings, bool LimitReached);

public static class ProjectFiles
{
    public const int MaximumIndexedFiles = 100_000;
    private const int MaximumGitOutputCharacters = 32 * 1024 * 1024;

    public static async Task<ProjectFileIndex> IndexAsync(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var warnings = new List<string>();
        try
        {
            var fromGit = await ReadGitFilesAsync(root, cancellationToken);
            if (fromGit is not null) return fromGit;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            warnings.Add("Git is unavailable; ignore rules cannot be applied to this file list.");
        }
        catch (IOException exception)
        {
            warnings.Add("Git file listing failed: " + exception.Message);
        }
        return await Task.Run(() => Walk(root, warnings, cancellationToken), cancellationToken);
    }

    private static async Task<ProjectFileIndex?> ReadGitFilesAsync(string root, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in new[] { "-C", root, "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var files = new List<ProjectFile>();
        var seen = new HashSet<string>(PathComparer);
        var pending = new StringBuilder();
        var characters = new char[8192];
        var outputLength = 0;
        var limited = false;
        try
        {
            int count;
            while ((count = await process.StandardOutput.ReadAsync(characters, cancellationToken)) > 0)
            {
                outputLength += count;
                if (outputLength > MaximumGitOutputCharacters) { limited = true; break; }
                for (var index = 0; index < count; index++)
                {
                    if (characters[index] != '\0') { pending.Append(characters[index]); continue; }
                    var path = Path.GetFullPath(Path.Combine(root, pending.ToString()));
                    pending.Clear();
                    if (IsWithin(root, path) && seen.Add(path) && IsRegularFileWithoutLinks(root, path))
                        files.Add(new(path, Path.GetRelativePath(root, path)));
                    if (files.Count >= MaximumIndexedFiles) { limited = true; break; }
                }
                if (limited) break;
            }
            if (limited && !process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            var errorText = await errors;
            if (!limited && process.ExitCode != 0)
            {
                if (errorText.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)) return null;
                throw new IOException(errorText.Trim());
            }
            files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
            return new(root, files, [], limited);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            // Observe the redirected error stream even if reading stdout was cancelled.
            try { await errors; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    private static ProjectFileIndex Walk(string root, List<string> warnings, CancellationToken cancellationToken)
    {
        var files = new List<ProjectFile>();
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.Name == ".git") continue;
                    if ((entry.Attributes & FileAttributes.Directory) != 0) directories.Push(entry.FullName);
                    else files.Add(new(entry.FullName, Path.GetRelativePath(root, entry.FullName)));
                    if (files.Count >= MaximumIndexedFiles || directories.Count >= MaximumIndexedFiles)
                        return new(root, files, warnings, true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (warnings.Count < 50) warnings.Add(Path.GetRelativePath(root, directory) + ": " + exception.Message);
            }
        }
        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return new(root, files, warnings, false);
    }

    private static bool IsRegularFileWithoutLinks(string root, string path)
    {
        if (!File.Exists(path)) return false;
        for (var current = path; !PathComparer.Equals(current, root); current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public static class FuzzyMatcher
{
    public static int? Score(string candidate, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        var previous = new int[candidate.Length];
        Array.Fill(previous, int.MinValue);
        for (var queryIndex = 0; queryIndex < query.Length; queryIndex++)
        {
            var current = new int[candidate.Length];
            Array.Fill(current, int.MinValue);
            var bestPrefix = int.MinValue;
            for (var index = 0; index < candidate.Length; index++)
            {
                if (index > 0 && previous[index - 1] != int.MinValue)
                    bestPrefix = Math.Max(bestPrefix, previous[index - 1] + index - 1);
                if (char.ToUpperInvariant(candidate[index]) != char.ToUpperInvariant(query[queryIndex])) continue;
                var boundary = index == 0 || candidate[index - 1] is '/' or '\\' or '_' or '-' or '.' ||
                    (char.IsLower(candidate[index - 1]) && char.IsUpper(candidate[index]));
                var reward = 10 + (boundary ? 18 : 0);
                if (queryIndex == 0) current[index] = reward - index;
                else if (bestPrefix != int.MinValue)
                {
                    current[index] = bestPrefix - index + reward;
                    if (index > 0 && previous[index - 1] != int.MinValue)
                        current[index] = Math.Max(current[index], previous[index - 1] + reward + 14);
                }
            }
            previous = current;
        }
        var score = previous.Length == 0 ? int.MinValue : previous.Max();
        return score == int.MinValue ? null : score;
    }

    public static IReadOnlyList<ProjectFile> Rank(IEnumerable<ProjectFile> files, string query, int maximum = 100, CancellationToken cancellationToken = default) => files
        .Select(file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (File: file, Score: Score(file.RelativePath, query), NameScore: Score(Path.GetFileName(file.RelativePath), query));
        })
        .Where(item => item.Score is not null)
        .OrderByDescending(item => item.NameScore is not null ? item.NameScore.Value + 100 : item.Score!.Value)
        .ThenBy(item => item.File.RelativePath.Length)
        .ThenBy(item => item.File.RelativePath, StringComparer.OrdinalIgnoreCase)
        .Take(maximum).Select(item => item.File).ToArray();
}

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Zyber.Services;

public sealed record GitChange(string Path, string? OriginalPath, char Index, char WorkingTree, bool Untracked, bool Conflict)
{
    public bool HasStaged => Index is not ('.' or '?');
    public bool HasUnstaged => Untracked || WorkingTree != '.';
}
public sealed record GitSnapshot(string Branch, string? Upstream, int Ahead, int Behind, IReadOnlyList<GitChange> Changes);
public sealed record GitCommit(string Hash, string Author, DateTimeOffset Date, string Subject)
{
    public string ShortHash => Hash[..Math.Min(7, Hash.Length)];
    public string Description => $"{Author} · {Date.LocalDateTime:g} · {ShortHash}";
}

public sealed class GitRepository(string root, string metadataDirectory)
{
    public string Root { get; } = root;
    public string MetadataDirectory { get; } = metadataDirectory;

    public static async Task<GitRepository?> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        var top = await RunAsync(directory, ["rev-parse", "--show-toplevel"], allowFailure: true, cancellationToken: cancellationToken);
        if (top.Code != 0) return null;
        var root = top.Output.TrimEnd('\r', '\n');
        var metadata = await RunAsync(root, ["rev-parse", "--absolute-git-dir"], cancellationToken: cancellationToken);
        return new(root, metadata.Output.TrimEnd('\r', '\n'));
    }

    public async Task<GitSnapshot> StatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(Root, ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"], cancellationToken: cancellationToken);
        var records = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var branch = "";
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var changes = new List<GitChange>();
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = record[14..];
            else if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal)) upstream = record[18..];
            else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = record[12..].Split(' ');
                if (parts.Length == 2) { int.TryParse(parts[0].TrimStart('+'), out ahead); int.TryParse(parts[1].TrimStart('-'), out behind); }
            }
            else if (record.StartsWith("? ", StringComparison.Ordinal)) changes.Add(new(record[2..], null, '?', '?', true, false));
            else if (record.StartsWith("1 ", StringComparison.Ordinal) || record.StartsWith("2 ", StringComparison.Ordinal) || record.StartsWith("u ", StringComparison.Ordinal))
            {
                var renamed = record[0] == '2';
                var conflict = record[0] == 'u';
                var parts = record.Split(' ', renamed ? 10 : conflict ? 11 : 9);
                if (parts.Length < (renamed ? 10 : conflict ? 11 : 9) || parts[1].Length != 2) throw new IOException("Invalid Git status record.");
                string? original = null;
                if (renamed) { if (++index >= records.Length) throw new IOException("Git omitted a rename source."); original = records[index]; }
                changes.Add(new(parts[^1], original, parts[1][0], parts[1][1], false, conflict));
            }
        }
        return new(branch, upstream, ahead, behind, changes);
    }

    public async Task<IReadOnlyList<GitCommit>> HistoryAsync(CancellationToken cancellationToken = default)
    {
        var head = await RunAsync(Root, ["rev-parse", "--verify", "HEAD"], allowFailure: true, cancellationToken: cancellationToken);
        if (head.Code != 0) return [];
        var result = await RunAsync(Root, ["log", "-100", "-z", "--format=%H%x00%an%x00%at%x00%s"], cancellationToken: cancellationToken);
        var fields = result.Output.Split('\0');
        var commits = new List<GitCommit>();
        for (var index = 0; index + 3 < fields.Length; index += 4)
        {
            if (!long.TryParse(fields[index + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)) throw new IOException("Invalid Git history date.");
            commits.Add(new(fields[index], fields[index + 1], DateTimeOffset.FromUnixTimeSeconds(timestamp), fields[index + 3]));
        }
        return commits;
    }

    public async Task StageAsync(GitChange change, CancellationToken cancellationToken = default) =>
        await RunAsync(Root, new[] { "add", "--" }.Concat(Paths(change)).ToArray(), cancellationToken: cancellationToken);
    public async Task StageAllAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(Root, ["add", "--all", "--", "."], cancellationToken: cancellationToken);
    public async Task UnstageAsync(GitChange change, CancellationToken cancellationToken = default)
    {
        var head = await RunAsync(Root, ["rev-parse", "--verify", "HEAD"], allowFailure: true, cancellationToken: cancellationToken);
        var command = head.Code == 0 ? new[] { "restore", "--staged", "--" } : new[] { "rm", "--cached", "--force", "--ignore-unmatch", "--" };
        await RunAsync(Root, command.Concat(Paths(change)).ToArray(), cancellationToken: cancellationToken);
    }
    public async Task CommitAsync(string message, bool includeTracked, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new IOException("Enter a commit message.");
        if (includeTracked) await RunAsync(Root, ["add", "--update", "--", "."], cancellationToken: cancellationToken);
        await RunAsync(Root, ["commit", "--file=-"], input: message, cancellationToken: cancellationToken);
    }
    public async Task PushAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(Root, ["push"], cancellationToken: cancellationToken);
    public async Task FetchAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(Root, ["fetch"], cancellationToken: cancellationToken);
    public async Task<string> DiffAsync(GitChange? change = null, bool staged = false, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color" };
        if (change?.Untracked == true)
        {
            arguments.AddRange(["--no-index", "--", "/dev/null", Path.Combine(Root, change.Path)]);
            var result = await RunAsync(Root, arguments, allowFailure: true, cancellationToken: cancellationToken);
            if (result.Code is not (0 or 1)) throw new IOException(result.Error);
            return result.Output;
        }
        if (staged) arguments.Add("--cached");
        arguments.Add("--");
        if (change is not null) arguments.AddRange(Paths(change));
        return (await RunAsync(Root, arguments, cancellationToken: cancellationToken)).Output;
    }
    public async Task<string> CommitDiffAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit)) throw new IOException("Invalid commit identifier.");
        return (await RunAsync(Root, ["show", "--format=fuller", "--no-ext-diff", "--no-textconv", "--no-color", hash, "--"], cancellationToken: cancellationToken)).Output;
    }
    private static IEnumerable<string> Paths(GitChange change)
    {
        yield return change.Path;
        if (change.OriginalPath is not null) yield return change.OriginalPath;
    }

    private sealed record Result(int Code, string Output, string Error);
    private static async Task<Result> RunAsync(string directory, IReadOnlyList<string> arguments, string? input = null, bool allowFailure = false, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.ArgumentList.Add("--literal-pathspecs");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        using var cancellation = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        async Task<string> ReadAsync(StreamReader reader, int maximum)
        {
            var text = new StringBuilder();
            var buffer = new char[8192];
            int count;
            while ((count = await reader.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (text.Length + count > maximum) { timeout.Cancel(); throw new IOException("Git output exceeded the display limit; narrow the requested diff."); }
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        var output = ReadAsync(process.StandardOutput, 8 * 1024 * 1024);
        var errors = ReadAsync(process.StandardError, 1024 * 1024);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var result = new Result(process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
            if (!allowFailure && result.Code != 0) throw new IOException("Git " + arguments[0] + " failed: " + result.Error.Trim());
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (output.IsFaulted) await output.ConfigureAwait(false);
            if (errors.IsFaulted) await errors.ConfigureAwait(false);
            throw new IOException("Git operation timed out.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
        }
    }
}

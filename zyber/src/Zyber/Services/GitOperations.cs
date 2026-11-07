using System.Diagnostics;

namespace Zyber.Services;

public static class GitOperations
{
    public static async Task CloneAsync(string repository, string destination, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository)) throw new ArgumentException("Enter a repository URL or path.", nameof(repository));
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The destination already exists. Choose a new folder name.");
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in new[] { "clone", "--", repository, Path.GetFullPath(destination) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await output;
            var message = await errors;
            if (process.ExitCode != 0) throw new IOException("Clone failed: " + message.Trim());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, errors); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}

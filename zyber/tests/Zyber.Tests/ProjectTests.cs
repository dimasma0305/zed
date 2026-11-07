using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using Zyber.Views;
using Zyber.Services;
using Xunit;

namespace Zyber.Tests;

public sealed class ProjectTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "zyber-project-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;
    public ProjectTests() => Directory.CreateDirectory(root);
    private async Task<string> Write(string name, string text)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, Cancellation);
        return path;
    }
    private async Task Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(Cancellation);
        await process.WaitForExitAsync(Cancellation);
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task GitIndexHonorsIgnoreRulesButKeepsTrackedIgnoredFiles()
    {
        await Git("init", "--quiet");
        await Write(".gitignore", "*.log\nignored/\n");
        await Write("ignored/hidden.cs", "hidden");
        await Write("tracked.log", "tracked");
        await Git("add", "-f", "tracked.log");
        await Write("untracked.log", "ignored");
        await Write("src/file with spaces.cs", "visible");
        var index = await ProjectFiles.IndexAsync(root, Cancellation);
        Assert.Equal(new[] { ".gitignore", Path.Combine("src", "file with spaces.cs"), "tracked.log" }, index.Files.Select(file => file.RelativePath));
        Assert.Empty(index.Warnings);
        Assert.False(index.LimitReached);
    }

    [AvaloniaFact]
    public async Task GitPanelStagesCommitsAndOpensReadOnlyDiffs()
    {
        await Git("init", "--quiet");
        await Git("config", "user.name", "Zyber Test");
        await Git("config", "user.email", "test@example.invalid");
        await Git("config", "commit.gpgsign", "false");
        await Write("note.txt", "original\n");
        var repository = (await GitRepository.OpenAsync(root, Cancellation))!;
        await repository.StageAllAsync(Cancellation);
        await repository.CommitAsync("Initial file", false, Cancellation);
        await Write("note.txt", "changed\n");
        var window = new MainWindow();
        try
        {
            window.Show();
            await window.OpenFolderAsync(root);
            var panel = window.FindControl<GitPanel>("GitSidebar")!;
            Assert.True(panel.State.HasRepository);
            var unstaged = Assert.Single(panel.State.Changes);
            Assert.False(unstaged.Staged);
            await panel.StageAsync(unstaged);
            var staged = Assert.Single(panel.State.Changes);
            Assert.True(staged.Staged);
            await panel.ShowChangeAsync(staged);
            Assert.NotNull(window.State.ActiveDiff);
            var editor = window.FindControl<TextEditor>("Editor")!;
            Assert.True(editor.IsReadOnly);
            Assert.Contains("+changed", editor.Text);
            panel.State.CommitMessage = "Commit through the panel";
            await panel.CommitAsync();
            Assert.True(panel.State.IsClean);
            Assert.Equal("Commit through the panel", panel.State.History[0].Subject);
            Assert.Empty(panel.State.CommitMessage);
        }
        finally { await window.Code.DisposeAsync(); window.Close(); }
    }

    [Fact]
    public async Task GitStagesLiteralNamesAndKeepsStagedAndUnstagedContentSeparate()
    {
        await Git("init", "--quiet");
        var target = await Write("note[1].txt", "first version\n");
        await Write("note1.txt", "must remain untracked\n");
        var repository = (await GitRepository.OpenAsync(root, Cancellation))!;
        var initial = await repository.StatusAsync(Cancellation);
        await repository.StageAsync(initial.Changes.Single(change => change.Path == "note[1].txt"), Cancellation);
        await File.WriteAllTextAsync(target, "second version\n", Cancellation);
        var snapshot = await repository.StatusAsync(Cancellation);
        var changed = snapshot.Changes.Single(change => change.Path == "note[1].txt");
        Assert.True(changed.HasStaged);
        Assert.True(changed.HasUnstaged);
        Assert.True(snapshot.Changes.Single(change => change.Path == "note1.txt").Untracked);
        Assert.Contains("+first version", await repository.DiffAsync(changed, true, Cancellation));
        Assert.Contains("+second version", await repository.DiffAsync(changed, false, Cancellation));
        await repository.UnstageAsync(changed, Cancellation);
        Assert.All((await repository.StatusAsync(Cancellation)).Changes, change => Assert.True(change.Untracked));
        Assert.Equal("second version\n", await File.ReadAllTextAsync(target, Cancellation));
        Assert.Contains("+second version", await repository.DiffAsync((await repository.StatusAsync(Cancellation)).Changes.Single(change => change.Path == "note[1].txt"), cancellationToken: Cancellation));
    }

    [Fact]
    public async Task GitCommitHistoryRenameAndPushUseRealRepositoryState()
    {
        await Git("init", "--quiet");
        await Git("config", "user.name", "Zyber Test");
        await Git("config", "user.email", "test@example.invalid");
        await Git("config", "commit.gpgsign", "false");
        var repository = (await GitRepository.OpenAsync(root, Cancellation))!;
        Assert.Empty(await repository.HistoryAsync(Cancellation));
        await Write("original name.txt", "one\n");
        await repository.StageAllAsync(Cancellation);
        await repository.CommitAsync("Add café", false, Cancellation);
        var history = await repository.HistoryAsync(Cancellation);
        Assert.Equal("Add café", Assert.Single(history).Subject);
        Assert.Contains("original name.txt", await repository.CommitDiffAsync(history[0].Hash, Cancellation));
        await Git("mv", "original name.txt", "renamed name.txt");
        var rename = Assert.Single((await repository.StatusAsync(Cancellation)).Changes);
        Assert.Equal("original name.txt", rename.OriginalPath);
        Assert.Equal("renamed name.txt", rename.Path);
        await repository.UnstageAsync(rename, Cancellation);
        Assert.True(File.Exists(Path.Combine(root, "renamed name.txt")));
        await repository.StageAllAsync(Cancellation);
        await repository.CommitAsync("Rename file", false, Cancellation);
        var remote = Path.Combine(root, "remote.git");
        await Write(".gitignore", "remote.git/\n");
        await Git("init", "--bare", "--quiet", remote);
        await Git("remote", "add", "origin", remote);
        await Git("push", "--set-upstream", "origin", "HEAD");
        await Write("renamed name.txt", "two\n");
        await Write("untracked.txt", "not in commit\n");
        await repository.CommitAsync("Update tracked file", true, Cancellation);
        var ahead = await repository.StatusAsync(Cancellation);
        Assert.Equal(1, ahead.Ahead);
        Assert.Contains(ahead.Changes, change => change.Path == "untracked.txt" && change.Untracked);
        await repository.PushAsync(Cancellation);
        await repository.FetchAsync(Cancellation);
        Assert.Equal(0, (await repository.StatusAsync(Cancellation)).Ahead);
        Assert.Equal("Update tracked file", (await repository.HistoryAsync(Cancellation))[0].Subject);
        await Assert.ThrowsAsync<IOException>(() => repository.CommitAsync("  ", false, Cancellation));
    }

    [Fact]
    public async Task CloneCopiesARepositoryAndRefusesAnExistingDestination()
    {
        await Git("init", "--quiet");
        await Write("sample.txt", "cloned content");
        await Git("add", "sample.txt");
        await Git("-c", "user.name=Zyber Test", "-c", "user.email=test@example.invalid", "commit", "--quiet", "-m", "Test fixture");
        var destination = Path.Combine(root, "clone");
        await GitOperations.CloneAsync(root, destination, Cancellation);
        Assert.Equal("cloned content", await File.ReadAllTextAsync(Path.Combine(destination, "sample.txt"), Cancellation));
        await Assert.ThrowsAsync<IOException>(() => GitOperations.CloneAsync(root, destination, Cancellation));
    }

    [Fact]
    public void FuzzyFinderPrefersFilenameAndSupportsSubsequence()
    {
        var files = new[] { new ProjectFile("a", "MainWindow/deep/Other.cs"), new ProjectFile("b", "src/MainWindow.cs"), new ProjectFile("c", "src/App.cs") };
        var result = FuzzyMatcher.Rank(files, "mw", cancellationToken: Cancellation);
        Assert.Equal(new[] { "b", "a" }, result.Select(file => file.FullPath));
        Assert.Empty(FuzzyMatcher.Rank(files, "xyz", cancellationToken: Cancellation));
    }

    [Fact]
    public async Task SearchPreservesUtf16PositionsAndReportsSkippedBinaryFiles()
    {
        await Write("text.txt", "first\r\n😀 cat CAT scatter cat_ café\r\ncat\n");
        await File.WriteAllBytesAsync(Path.Combine(root, "binary.bin"), new byte[] { 0, 1, 0, 2 }, Cancellation);
        var index = await ProjectFiles.IndexAsync(root, Cancellation);
        var result = await ProjectSearch.FindAsync(index, new("cat", WholeWord: true), Cancellation);
        Assert.Equal(new[] { (2, 4), (2, 8), (3, 1) }, result.Hits.Select(hit => (hit.Line, hit.Column)));
        Assert.Equal(10, result.Hits[0].Offset);
        Assert.Equal(1, result.FilesSkipped);
        Assert.Single(result.Warnings);
        var sensitive = await ProjectSearch.FindAsync(index, new("cat", CaseSensitive: true, WholeWord: true), Cancellation);
        Assert.Equal(2, sensitive.Hits.Count);
        var regex = await ProjectSearch.FindAsync(index, new("^cat$", RegularExpression: true), Cancellation);
        Assert.Equal(3, Assert.Single(regex.Hits).Line);
    }

    [Fact]
    public async Task SearchReportsItsLimitAndRejectsInvalidRegex()
    {
        await Write("many.txt", new string('x', ProjectSearch.MaximumResults + 10));
        var index = await ProjectFiles.IndexAsync(root, Cancellation);
        var report = await ProjectSearch.FindAsync(index, new("x"), Cancellation);
        Assert.True(report.LimitReached);
        Assert.Equal(ProjectSearch.MaximumResults, report.Hits.Count);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ProjectSearch.FindAsync(index, new("[", RegularExpression: true), Cancellation));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectSearch.FindAsync(index, new("x"), cancelled.Token));
    }

    [Fact]
    public async Task RecentProjectsSurviveRestartAndDeduplicate()
    {
        var path = Path.Combine(root, "history.json");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        var history = new RecentProjects(path);
        await history.AddAsync(first);
        await history.AddAsync(second);
        await history.AddAsync(first);
        Assert.Equal(new[] { first, second }, await new RecentProjects(path).ReadAsync());
        await File.WriteAllTextAsync(path, "broken JSON", Cancellation);
        await Assert.ThrowsAsync<IOException>(() => history.AddAsync(root));
        Assert.Equal("broken JSON", await File.ReadAllTextAsync(path, Cancellation));
    }
    public void Dispose()
    {
        // Git objects can be read-only on Windows, where recursive directory deletion preserves them.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, true);
    }
}

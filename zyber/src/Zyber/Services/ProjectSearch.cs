using System.Text.RegularExpressions;

namespace Zyber.Services;

public sealed record SearchOptions(string Query, bool CaseSensitive = false, bool WholeWord = false, bool RegularExpression = false);
public sealed record SearchHit(string FullPath, string RelativePath, int Line, int Column, int Offset, int Length, string Preview, string Fingerprint)
{
    public string Location => $"{RelativePath}:{Line}:{Column}";
}
public sealed record SearchReport(IReadOnlyList<SearchHit> Hits, int FilesSearched, int FilesSkipped, bool LimitReached, IReadOnlyList<string> Warnings);

public static class ProjectSearch
{
    public const int MaximumResults = 2000;

    public static async Task<SearchReport> FindAsync(ProjectFileIndex index, SearchOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(options.Query)) return new([], 0, 0, false, index.Warnings);
        var expression = options.RegularExpression ? options.Query : Regex.Escape(options.Query);
        if (options.WholeWord) expression = @"(?<![\p{L}\p{N}\p{M}_])(?:" + expression + @")(?![\p{L}\p{N}\p{M}_])";
        var flags = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!options.CaseSensitive) flags |= RegexOptions.IgnoreCase;
        var regex = new Regex(expression, flags, TimeSpan.FromMilliseconds(250));
        var hits = new List<SearchHit>();
        var warnings = new List<string>(index.Warnings);
        var searched = 0;
        var skipped = 0;
        foreach (var file in index.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await TextFileStore.ReadAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
                searched++;
                var line = 1;
                var lineStart = 0;
                foreach (Match match in regex.Matches(snapshot.Text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var newline = snapshot.Text.IndexOf('\n', lineStart);
                    while (newline >= 0 && newline < match.Index)
                    {
                        line++; lineStart = newline + 1;
                        newline = snapshot.Text.IndexOf('\n', lineStart);
                    }
                    var lineEnd = newline < 0 ? snapshot.Text.Length : newline;
                    var previewStart = Math.Max(lineStart, match.Index - 80);
                    var previewLength = Math.Min(240, lineEnd - previewStart);
                    var preview = snapshot.Text.Substring(previewStart, Math.Max(0, previewLength)).TrimEnd('\r');
                    hits.Add(new(file.FullPath, file.RelativePath, line, match.Index - lineStart + 1, match.Index, match.Length, preview, snapshot.Fingerprint));
                    if (hits.Count >= MaximumResults) return new(hits, searched, skipped, true, warnings);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
            {
                skipped++;
                if (warnings.Count < 50) warnings.Add(file.RelativePath + ": " + exception.Message);
            }
        }
        return new(hits, searched, skipped, index.LimitReached, warnings);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zyber.Languages;

public sealed record LspPosition([property: JsonPropertyName("line")] int Line, [property: JsonPropertyName("character")] int Character);
public sealed record LspRange([property: JsonPropertyName("start")] LspPosition Start, [property: JsonPropertyName("end")] LspPosition End);
public sealed record LspTextEdit([property: JsonPropertyName("range")] LspRange Range, [property: JsonPropertyName("newText")] string NewText);
public sealed record LspDiagnostic(LspRange Range, int Severity, string Message, string? Source);
public sealed record PublishedDiagnostics(string Uri, int? Version, IReadOnlyList<LspDiagnostic> Diagnostics);
public sealed record LspLocation(string Uri, LspRange Range)
{
    public string Label => $"{Path.GetFileName(new Uri(Uri).LocalPath)}:{Range.Start.Line + 1}:{Range.Start.Character + 1}";
}
public sealed record LspCompletion(string Label, string? Detail, string? Documentation, string InsertText, LspTextEdit? Edit, IReadOnlyList<LspTextEdit> AdditionalEdits, string SortText);

public static class LspText
{
    public static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
    public static LspPosition Position(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var line = 0;
        var start = 0;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    if (index + 1 == offset) return new(line, index - start);
                    index++;
                }
                line++; start = index + 1;
            }
            else if (text[index] == '\n') { line++; start = index + 1; }
        }
        return new(line, offset - start);
    }
    public static int Offset(string text, LspPosition position)
    {
        if (position.Line < 0 || position.Character < 0) throw new IOException("Language server returned a negative text position.");
        var start = 0;
        for (var line = 0; line < position.Line; line++)
        {
            var newline = text.IndexOfAny(['\r', '\n'], start);
            if (newline < 0) return text.Length;
            start = newline + 1;
            if (text[newline] == '\r' && start < text.Length && text[start] == '\n') start++;
        }
        var end = text.IndexOfAny(['\r', '\n'], start);
        if (end < 0) end = text.Length;
        // LSP specifies that oversized line/character positions clamp to the document/line end.
        return start + Math.Min(position.Character, end - start);
    }
    public static IReadOnlyList<(int Offset, int Length, string Text)> ValidateEdits(string text, IEnumerable<LspTextEdit> edits)
    {
        var converted = edits.Select(edit =>
        {
            var start = Offset(text, edit.Range.Start);
            var end = Offset(text, edit.Range.End);
            if (end < start) throw new IOException("Language server returned a reversed text range.");
            return (Offset: start, Length: end - start, Text: edit.NewText);
        }).OrderByDescending(edit => edit.Offset).ThenByDescending(edit => edit.Length).ToArray();
        var next = text.Length + 1;
        foreach (var edit in converted)
        {
            if (edit.Offset + edit.Length > next) throw new IOException("Language server returned overlapping edits.");
            next = edit.Offset;
        }
        return converted;
    }
    public static string? Documentation(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String) return item.GetString();
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var value)) return value.GetString();
        if (item.ValueKind == JsonValueKind.Array) return string.Join("\n\n", item.EnumerateArray().Select(Documentation).Where(value => value is not null));
        return null;
    }
    public static LspRange Range(JsonElement element) => JsonSerializer.Deserialize<LspRange>(element) ?? throw new IOException("Language server omitted a text range.");
    public static LspTextEdit Edit(JsonElement element) => JsonSerializer.Deserialize<LspTextEdit>(element) ?? throw new IOException("Language server omitted a text edit.");
}

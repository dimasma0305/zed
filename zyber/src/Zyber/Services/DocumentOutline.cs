using System.Text;
using System.Text.Json;
using Zyber.Languages;

namespace Zyber.Services;

public sealed record OutlineSymbol(string Name, int Kind, int Start, int End, int SelectionStart, int SelectionEnd, IReadOnlyList<OutlineSymbol> Children);
public sealed record OutlineSnapshot(string Text, IReadOnlyList<OutlineSymbol> Symbols, string? Message = null);

public static class DocumentOutline
{
    public const int MaximumSymbols = 5000;
    private sealed record JsonSymbol(string Name, int Start, int End, int SelectionEnd, IReadOnlyList<JsonSymbol> Children);

    public static OutlineSnapshot Json(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 });
            if (!reader.Read()) return new(text, [], "No symbols in this document");
            var count = 0;
            var raw = ReadValue(ref reader, ref count, cancellationToken);
            if (reader.Read()) throw new JsonException("Unexpected content after the JSON value.");
            // Translate sorted UTF-8 token boundaries in one pass, including non-BMP characters.
            var offsets = new SortedSet<int> { 0 };
            void Collect(IEnumerable<JsonSymbol> symbols)
            {
                foreach (var symbol in symbols) { offsets.Add(symbol.Start); offsets.Add(symbol.End); offsets.Add(symbol.SelectionEnd); Collect(symbol.Children); }
            }
            Collect(raw);
            var mapped = new Dictionary<int, int>();
            var previous = 0;
            var characters = 0;
            foreach (var offset in offsets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                characters += Encoding.UTF8.GetCharCount(bytes.AsSpan(previous, offset - previous));
                mapped[offset] = characters;
                previous = offset;
            }
            IReadOnlyList<OutlineSymbol> Convert(IEnumerable<JsonSymbol> symbols) => symbols.Select(symbol => new OutlineSymbol(symbol.Name, 7,
                mapped[symbol.Start], mapped[symbol.End], mapped[symbol.Start] + 1, mapped[symbol.SelectionEnd] - 1, Convert(symbol.Children))).ToArray();
            return new(text, Convert(raw));
        }
        catch (JsonException exception) { return new(text, [], "JSON outline: " + exception.Message); }
    }

    private static IReadOnlyList<JsonSymbol> ReadValue(ref Utf8JsonReader reader, ref int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<JsonSymbol> symbols = [];
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a property name.");
                if (++count > MaximumSymbols) throw new JsonException($"Outline is limited to {MaximumSymbols} symbols.");
                var name = reader.GetString()!;
                var start = checked((int)reader.TokenStartIndex);
                // ValueSpan excludes quotes and handles escaped property names without changing source offsets.
                var selectionEnd = start + reader.ValueSpan.Length + 2;
                if (!reader.Read()) throw new JsonException("Expected a property value.");
                var children = ReadValue(ref reader, ref count, cancellationToken);
                symbols.Add(new(name, start, checked((int)reader.BytesConsumed), selectionEnd, children));
            }
            if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Unclosed JSON object.");
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) symbols.AddRange(ReadValue(ref reader, ref count, cancellationToken));
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Unclosed JSON array.");
        }
        return symbols;
    }

    public static OutlineSnapshot FromLanguageServer(string text, string path, JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null) return new(text, []);
        if (result.ValueKind != JsonValueKind.Array) throw new IOException("Language server returned invalid document symbols.");
        var count = 0;
        var offsets = new LineOffsets(text);
        IReadOnlyList<OutlineSymbol> Parse(JsonElement items, int depth)
        {
            if (depth > 128) throw new IOException("Language server returned too many nested symbols.");
            List<OutlineSymbol> symbols = [];
            foreach (var item in items.EnumerateArray())
            {
                if (++count > MaximumSymbols) throw new IOException($"Outline is limited to {MaximumSymbols} symbols.");
                JsonElement range;
                JsonElement selection;
                if (item.TryGetProperty("location", out var location))
                {
                    if (!Uri.TryCreate(location.GetProperty("uri").GetString(), UriKind.Absolute, out var uri) || !uri.IsFile || !ProjectFiles.PathComparer.Equals(uri.LocalPath, path)) continue;
                    range = selection = location.GetProperty("range");
                }
                else
                {
                    range = item.GetProperty("range");
                    selection = item.TryGetProperty("selectionRange", out var selected) ? selected : range;
                }
                var extent = LspText.Range(range);
                var nameRange = LspText.Range(selection);
                var start = offsets.Get(extent.Start); var end = offsets.Get(extent.End);
                var selectedStart = offsets.Get(nameRange.Start); var selectedEnd = offsets.Get(nameRange.End);
                if (end < start || selectedStart < start || selectedEnd > end || selectedEnd < selectedStart) throw new IOException("Language server returned invalid symbol ranges.");
                symbols.Add(new(item.GetProperty("name").GetString() ?? "", item.GetProperty("kind").GetInt32(), start, end, selectedStart, selectedEnd,
                    item.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array ? Parse(children, depth + 1) : []));
            }
            return symbols.OrderBy(symbol => symbol.Start).ToArray();
        }
        return new(text, Parse(result, 0));
    }

    private sealed class LineOffsets
    {
        private readonly List<int> starts = [0];
        private readonly List<int> ends = [];
        private readonly int length;
        public LineOffsets(string text)
        {
            length = text.Length;
            for (var index = 0; index < text.Length; index++)
                if (text[index] is '\r' or '\n')
                {
                    ends.Add(index);
                    if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    starts.Add(index + 1);
                }
            ends.Add(text.Length);
        }
        public int Get(LspPosition position)
        {
            if (position.Line < 0 || position.Character < 0) throw new IOException("Language server returned a negative symbol position.");
            return position.Line >= starts.Count ? length : starts[position.Line] + Math.Min(position.Character, ends[position.Line] - starts[position.Line]);
        }
    }
}

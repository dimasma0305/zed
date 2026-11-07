using System.Text;
using System.Text.Json;

namespace Zyber.Services;

public sealed class ThemeCatalog
{
    public IReadOnlyList<ZedTheme> Themes { get; }
    public IReadOnlyList<string> Warnings { get; }
    public static string ConfigurationDirectory(string application) => Path.Combine(
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        OperatingSystem.IsWindows() ? application : application.ToLowerInvariant());
    public static string UserThemeDirectory => Path.Combine(ConfigurationDirectory("Zyber"), "themes");
    public static string SettingsPath => Path.Combine(ConfigurationDirectory("Zyber"), "settings.json");
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public ThemeCatalog(IEnumerable<string>? directories = null)
    {
        var themes = ZedTheme.BuiltInThemes().ToDictionary(theme => theme.Name, StringComparer.Ordinal);
        List<string> warnings = [];
        foreach (var directory in directories ?? DefaultDirectories())
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false, MaxRecursionDepth = 8 };
                var roots = Path.GetFileName(directory) == "installed"
                    ? Directory.EnumerateDirectories(directory, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
                        .Select(extension => Path.Combine(extension, "themes")).Where(Directory.Exists)
                    : [directory];
                var files = roots.SelectMany(root => Directory.EnumerateFiles(root, "*.json", options)).Take(2001).Order(StringComparer.Ordinal).ToArray();
                if (files.Length > 2000) warnings.Add($"Theme directory has more than 2000 JSON files: {directory}");
                foreach (var path in files.Take(2000))
                {
                    try
                    {
                        foreach (var theme in ReadFamily(path))
                        {
                            themes[theme.Name] = theme;
                            warnings.AddRange(theme.Warnings);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                    { warnings.Add($"Could not load {Path.GetFileName(path)}: {exception.Message}"); }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { warnings.Add($"Could not scan themes in {directory}: {exception.Message}"); }
        }
        Themes = themes.Values.OrderBy(theme => theme.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Warnings = warnings;
    }

    public static IReadOnlyList<ZedTheme> ReadFamily(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 8 * 1024 * 1024) throw new IOException("Theme files must be smaller than 8 MiB.");
        using var document = JsonDocument.Parse(stream, Options);
        var themes = document.RootElement.GetProperty("themes");
        if (themes.ValueKind != JsonValueKind.Array || themes.GetArrayLength() == 0 || themes.GetArrayLength() > 512) throw new JsonException("Expected a family with 1–512 themes.");
        return themes.EnumerateArray().Select(theme => new ZedTheme(theme)).ToArray();
    }

    public static IEnumerable<string> DefaultDirectories()
    {
        foreach (var application in new[] { "Zed", "Zyber" })
        {
            var data = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            yield return Path.Combine(data, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? application : application.ToLowerInvariant(), "extensions", "installed");
        }
        yield return Path.Combine(ConfigurationDirectory("Zed"), "themes");
        yield return UserThemeDirectory;
    }

    public static async Task<string> ImportAsync(string path, string? directory = null)
    {
        // Validate and copy the same snapshot; never overwrite an existing imported family.
        var snapshot = await TextFileStore.ReadAsync(path);
        if (Encoding.UTF8.GetByteCount(snapshot.Text) > 8 * 1024 * 1024) throw new IOException("Theme files must be smaller than 8 MiB.");
        using var document = JsonDocument.Parse(snapshot.Text, Options);
        var family = document.RootElement.GetProperty("themes");
        if (family.ValueKind != JsonValueKind.Array || family.GetArrayLength() is < 1 or > 512) throw new JsonException("Expected a family with 1–512 themes.");
        var themes = family.EnumerateArray().Select(theme => new ZedTheme(theme)).ToArray();
        directory ??= UserThemeDirectory;
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        await TextFileStore.WriteAsync(destination, snapshot.Text, new UTF8Encoding(false), null);
        return themes[0].Name;
    }

    public static async Task SaveSelectionAsync(string name, string? path = null)
    {
        path ??= SettingsPath;
        var snapshot = File.Exists(path) ? await TextFileStore.ReadAsync(path) : null;
        var text = ReplaceThemeSetting(snapshot?.Text ?? "{\n}\n", name);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await TextFileStore.WriteAsync(snapshot?.Path ?? path, text, snapshot?.Encoding ?? new UTF8Encoding(false), snapshot?.Fingerprint);
    }

    public static string ReplaceThemeSetting(string text, string name)
    {
        using var document = JsonDocument.Parse(text, Options);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Settings must be a JSON object.");
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        List<(int Start, int End)> values = [];
        var insertion = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 0) insertion = (int)reader.BytesConsumed;
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || reader.GetString() != "theme") continue;
            reader.Read();
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            values.Add((start, (int)reader.BytesConsumed));
        }
        var replacement = JsonSerializer.Serialize(name);
        // Replace every duplicate root theme key, leaving comments and other settings byte-for-byte intact.
        if (values.Count == 0)
        {
            var property = "\n  \"theme\": " + replacement + (document.RootElement.EnumerateObject().Any() ? "," : "");
            return Encoding.UTF8.GetString(bytes.AsSpan(0, insertion)) + property + Encoding.UTF8.GetString(bytes.AsSpan(insertion));
        }
        foreach (var (start, end) in values.AsEnumerable().Reverse())
        {
            text = Encoding.UTF8.GetString(bytes.AsSpan(0, start)) + replacement + Encoding.UTF8.GetString(bytes.AsSpan(end));
            bytes = Encoding.UTF8.GetBytes(text);
        }
        return text;
    }
}

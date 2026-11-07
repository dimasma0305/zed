using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;

namespace Zyber.Services;

public sealed class ZedTheme
{
    public string Name { get; }
    public bool IsLight { get; }
    public JsonElement Style { get; }
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private readonly Dictionary<string, Color> colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Color> playerColors = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Warnings { get; }

    public ZedTheme(JsonElement theme)
    {
        Name = theme.GetProperty("name").GetString() ?? throw new JsonException("Theme name is required.");
        if (string.IsNullOrWhiteSpace(Name)) throw new JsonException("Theme name cannot be empty.");
        var appearance = theme.GetProperty("appearance").GetString();
        if (appearance is not ("light" or "dark")) throw new JsonException("Theme appearance must be light or dark.");
        IsLight = appearance == "light";
        Style = theme.GetProperty("style").Clone();
        if (Style.ValueKind != JsonValueKind.Object) throw new JsonException("Theme style must be an object.");
        using var stream = typeof(ZedTheme).Assembly.GetManifestResourceStream("Zyber.ThemeDefaults.json")!;
        using var defaults = JsonDocument.Parse(stream);
        var baseline = defaults.RootElement.GetProperty(appearance);
        foreach (var property in baseline.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String) colors[property.Name] = ParseColor(property.Value.GetString()!);
        foreach (var property in baseline.GetProperty("players")[0].EnumerateObject())
            playerColors[property.Name] = ParseColor(property.Value.GetString()!);
        List<string> warnings = [];
        var explicitColors = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var property in Style.EnumerateObject())
        {
            if ((!colors.ContainsKey(property.Name) && property.Name is not ("scrollbar_thumb.background" or "version_control_conflict_ours_background" or "version_control_conflict_theirs_background")) || property.Value.ValueKind == JsonValueKind.Null) continue;
            if (TryColor(property.Value, out var color)) explicitColors[property.Name] = color;
            else warnings.Add($"{Name}: invalid color for {property.Name}; using its default.");
        }
        foreach (var name in new[] { "deleted", "created", "modified", "conflict", "error", "hidden" })
            if (explicitColors.TryGetValue(name, out var color) && !explicitColors.ContainsKey(name + ".background"))
                explicitColors[name + ".background"] = WithAlpha(color, (byte)Math.Round(color.A * .25));
        void Inherit(string target, string source)
        {
            if (!explicitColors.ContainsKey(target) && explicitColors.TryGetValue(source, out var color)) explicitColors[target] = color;
        }
        Inherit("scrollbar.thumb.background", "scrollbar_thumb.background");
        Inherit("scrollbar.thumb.active_background", "scrollbar.thumb.background");
        Inherit("search.active_match_background", "search.match_background");
        Inherit("pane_group.border", "border");
        Inherit("editor.document_highlight.bracket_background", "editor.document_highlight.read_background");
        Inherit("version_control.added", "created");
        Inherit("version_control.deleted", "deleted");
        Inherit("version_control.modified", "modified");
        Inherit("version_control.renamed", "modified");
        Inherit("version_control.conflict_marker.ours", "version_control_conflict_ours_background");
        Inherit("version_control.conflict_marker.theirs", "version_control_conflict_theirs_background");
        Inherit("version_control.conflict", "ignored");
        Inherit("version_control.ignored", "ignored");
        Inherit("vim.yank.background", "editor.document_highlight.read_background");
        Inherit("vim.helix_jump_label.foreground", "error");
        Inherit("minimap.thumb.border", "scrollbar.thumb.border");
        foreach (var state in new[] { "background", "hover_background", "active_background" })
            if (!explicitColors.ContainsKey("minimap.thumb." + state) && explicitColors.TryGetValue("scrollbar.thumb." + state, out var thumb))
                explicitColors["minimap.thumb." + state] = WithAlpha(thumb, Math.Min(thumb.A, (byte)179));
        if (!explicitColors.ContainsKey("panel.overlay_background") && explicitColors.TryGetValue("panel.background", out var panel))
            explicitColors["panel.overlay_background"] = WithAlpha(panel, 255);
        if (!explicitColors.ContainsKey("panel.overlay_hover") && explicitColors.TryGetValue("panel.background", out var surface) && explicitColors.TryGetValue("element.hover", out var hover))
        {
            byte Blend(byte under, byte over) => (byte)Math.Round(under * (1 - hover.A / 255d) + over * hover.A / 255d);
            explicitColors["panel.overlay_hover"] = Avalonia.Media.Color.FromRgb(Blend(surface.R, hover.R), Blend(surface.G, hover.G), Blend(surface.B, hover.B));
        }
        foreach (var kind in new[] { "added", "deleted" })
            if (explicitColors.TryGetValue("version_control." + kind, out var color))
                foreach (var (suffix, alpha) in new[] { ("background", IsLight ? .16 : .12), ("hollow_background", IsLight ? .08 : .06), ("hollow_border", IsLight ? .48 : .36) })
                    explicitColors.TryAdd("editor.diff_hunk." + kind + "." + suffix, WithAlpha(color, (byte)Math.Round(color.A * alpha)));
        foreach (var (name, color) in explicitColors) colors[name] = color;
        if (Style.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array && players.GetArrayLength() > 0 && players[0].ValueKind == JsonValueKind.Object)
            foreach (var property in players[0].EnumerateObject())
                if (TryColor(property.Value, out var color)) playerColors[property.Name] = color;
        if (!explicitColors.ContainsKey("element.selection_background"))
        {
            var selection = playerColors["selection"];
            colors["element.selection_background"] = selection.A == 255 ? WithAlpha(selection, 64) : selection;
        }
        if (Style.TryGetProperty("background.appearance", out var background) && background.ValueKind == JsonValueKind.String && background.GetString() is "blurred" or "transparent")
            warnings.Add($"{Name}: window transparency and blur are not supported yet.");
        Warnings = warnings;
    }

    private static Color WithAlpha(Color color, byte alpha) => Avalonia.Media.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static bool TryColor(JsonElement value, out Color color)
    {
        color = default;
        if (value.ValueKind != JsonValueKind.String) return false;
        try { color = ParseColor(value.GetString()!); return true; }
        catch (FormatException) { return false; }
    }

    public static IReadOnlyList<ZedTheme> BuiltInThemes()
    {
        var result = new List<ZedTheme>();
        var assembly = typeof(ZedTheme).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("Zyber.Themes.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new IOException("Missing bundled theme: " + name);
            using var document = JsonDocument.Parse(stream, JsonOptions);
            result.AddRange(document.RootElement.GetProperty("themes").EnumerateArray().Select(theme => new ZedTheme(theme)));
        }
        return result;
    }

    public Color Color(string token) => colors.TryGetValue(token, out var color) ? color : colors["text"];
    public IBrush Brush(string token) => new SolidColorBrush(Color(token));
    public Color SyntaxColor(string token) => SyntaxStyle(token) is { } style && style.TryGetProperty("color", out var value) && TryColor(value, out var color) ? color : Color("editor.foreground");
    public JsonElement? SyntaxStyle(string token) => Style.TryGetProperty("syntax", out var syntax) && syntax.ValueKind == JsonValueKind.Object && syntax.TryGetProperty(token, out var style) && style.ValueKind == JsonValueKind.Object ? style : null;
    public Color PlayerColor(string token) => playerColors.TryGetValue(token, out var color) ? color : Color("text.accent");

    public static Color ParseColor(string text)
    {
        text = text.Trim();
        if (!text.StartsWith('#') || text.Length is not (4 or 5 or 7 or 9)) throw new FormatException("Expected #rgb, #rgba, #rrggbb or #rrggbbaa.");
        if (text.Length is 4 or 5) text = "#" + string.Concat(text.Skip(1).Select(character => new string(character, 2)));
        // Zed uses RGBA; Avalonia's eight-digit hexadecimal notation is ARGB.
        return Avalonia.Media.Color.FromArgb(text.Length == 9 ? byte.Parse(text.AsSpan(7, 2), NumberStyles.HexNumber) : (byte)255,
            byte.Parse(text.AsSpan(1, 2), NumberStyles.HexNumber), byte.Parse(text.AsSpan(3, 2), NumberStyles.HexNumber), byte.Parse(text.AsSpan(5, 2), NumberStyles.HexNumber));
    }

    public void Apply(Application application)
    {
        foreach (var (name, color) in colors) application.Resources["zed." + name] = new SolidColorBrush(color);
        application.Resources["TreeViewItemMinHeight"] = 26d;
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled", "Selected", "SelectedPointerOver", "SelectedPressed", "SelectedDisabled" })
        {
            var selected = state.StartsWith("Selected", StringComparison.Ordinal);
            application.Resources["TreeViewItemBackground" + state] = Brush(selected ? "element.selected" : state switch
            {
                "PointerOver" => "ghost_element.hover", "Pressed" => "ghost_element.active", "Disabled" => "ghost_element.disabled", _ => "ghost_element.background"
            });
            application.Resources["TreeViewItemForeground" + state] = Brush(state.EndsWith("Disabled", StringComparison.Ordinal) ? "text.disabled" : "text");
            application.Resources["TreeViewItemBorderBrush" + state] = Brush(selected ? "border.selected" : "border.transparent");
        }
        application.Resources["zed.accent.color"] = Color("text.accent");
    }

    public static (string Mode, string Dark, string Light, string? Warning) ReadThemePreference()
    {
        var configuration = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        foreach (var name in new[] { "Zyber", "Zed" })
        {
            var directory = OperatingSystem.IsWindows() ? name : name.ToLowerInvariant();
            var path = Path.Combine(configuration, directory, "settings.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
                if (!document.RootElement.TryGetProperty("theme", out var theme)) continue;
                if (theme.ValueKind == JsonValueKind.String) return ("explicit", theme.GetString()!, theme.GetString()!, null);
                if (theme.ValueKind != JsonValueKind.Object) return ("system", "One Dark", "One Light", "Theme setting must be a name or an object.");
                string Read(string key, string fallback) => theme.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
                return (Read("mode", "system"), Read("dark", "One Dark"), Read("light", "One Light"), null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return ("system", "One Dark", "One Light", "Could not read theme preference: " + exception.Message);
            }
        }
        return ("system", "One Dark", "One Light", null);
    }
}

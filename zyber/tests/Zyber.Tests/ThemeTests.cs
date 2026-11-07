using Avalonia.Media;
using Zyber.Services;
using Xunit;

namespace Zyber.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void ZedRgbaColorsPreserveTheirAlphaChannel()
    {
        Assert.Equal(Color.FromArgb(191, 47, 52, 62), ZedTheme.ParseColor("#2f343ebf"));
        Assert.Equal(Color.FromArgb(61, 116, 173, 232), ZedTheme.ParseColor("#74ade83d"));
        Assert.Equal(Color.FromArgb(255, 78, 90, 95), ZedTheme.ParseColor("#4e5a5f"));
    }

    [Theory]
    [InlineData("One Dark", "#282c33", "#acb2be", "#b477cf")]
    [InlineData("One Light", "#fafafa", "#242529", "#a449ab")]
    public void EditorUsesTheOriginalZedThemeValues(string name, string background, string foreground, string keyword)
    {
        var theme = ZedTheme.BuiltInThemes().Single(theme => theme.Name == name);
        Assert.Equal(Color.Parse(background), theme.Color("editor.background"));
        Assert.Equal(Color.Parse(foreground), theme.Color("editor.foreground"));
        Assert.Equal(Color.Parse(keyword), theme.SyntaxColor("keyword"));
    }

    [Fact]
    public void AllBundledThemesHaveEditorAndSelectionColors()
    {
        var themes = ZedTheme.BuiltInThemes();
        Assert.True(themes.Count >= 6);
        foreach (var theme in themes)
        {
            Assert.Equal(255, theme.Color("editor.background").A);
            Assert.NotEqual(0, theme.PlayerColor("selection").A);
            Assert.Equal(255, theme.SyntaxColor("primary").A);
        }
    }
}

public sealed class CustomThemeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "zyber-themes-" + Guid.NewGuid().ToString("N"));
    public CustomThemeTests() => Directory.CreateDirectory(root);
    private static ZedTheme Theme(string style, string appearance = "dark")
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"name\":\"Custom\",\"appearance\":\"" + appearance + "\",\"style\":" + style + "}");
        return new ZedTheme(document.RootElement);
    }

    [Theory]
    [InlineData("#1234", 68, 17, 34, 51)]
    [InlineData(" #abc ", 255, 170, 187, 204)]
    public void ShortRgbaColorsUseZedChannelOrder(string text, byte alpha, byte red, byte green, byte blue) =>
        Assert.Equal(Color.FromArgb(alpha, red, green, blue), ZedTheme.ParseColor(text));

    [Fact]
    public void PartialThemesUseZedDefaultsAndDerivedSelectionWithoutInheritingSyntax()
    {
        var theme = Theme("""{"editor.foreground":"#123456","created":"#00ff0080","players":[{"selection":"#abcd"}],"syntax":{"keyword":{"font_style":"italic"}}}""");
        Assert.Equal(Color.Parse("#111110"), theme.Color("editor.background"));
        Assert.Equal(Color.Parse("#123456"), theme.SyntaxColor("keyword"));
        Assert.Equal(Color.FromArgb(32, 0, 255, 0), theme.Color("created.background"));
        Assert.Equal(Color.FromArgb(221, 170, 187, 204), theme.Color("element.selection_background"));
        Assert.Equal("italic", theme.SyntaxStyle("keyword")!.Value.GetProperty("font_style").GetString());
        Assert.NotEqual(theme.Color("editor.background"), Theme("{}", "light").Color("editor.background"));
    }

    [Fact]
    public void InvalidOptionalColorFallsBackAndProducesWarning()
    {
        var theme = Theme("""{"editor.background":"wrong","background.appearance":"blurred"}""");
        Assert.Equal(Theme("{}").Color("editor.background"), theme.Color("editor.background"));
        Assert.Equal(2, theme.Warnings.Count);
        var aliases = Theme("""{"scrollbar_thumb.background":"#1234","created":"#00ff00","panel.background":"#11223380","element.hover":"#fff0"}""");
        Assert.Equal(ZedTheme.ParseColor("#1234"), aliases.Color("scrollbar.thumb.active_background"));
        Assert.Equal(Color.FromArgb(31, 0, 255, 0), aliases.Color("editor.diff_hunk.added.background"));
        Assert.Equal(Color.Parse("#112233"), aliases.Color("panel.overlay_hover"));
    }

    [Fact]
    public void CatalogLoadsJsoncFamiliesWithUserPrecedenceAndIsolatesMalformedFiles()
    {
        var first = Path.Combine(root, "extensions");
        var second = Path.Combine(root, "user");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "theme.json"), """{"themes":[{"name":"Custom","appearance":"dark","style":{"editor.background":"#123"}}]}""");
        File.WriteAllText(Path.Combine(second, "theme.json"), """{/* comment */"themes":[{"name":"Custom","appearance":"light","style":{"editor.background":"#abc",}},]}""");
        File.WriteAllText(Path.Combine(second, "broken.json"), "{");
        var catalog = new ThemeCatalog([first, second]);
        var custom = Assert.Single(catalog.Themes, theme => theme.Name == "Custom");
        Assert.True(custom.IsLight);
        Assert.Equal(Color.Parse("#aabbcc"), custom.Color("editor.background"));
        Assert.Single(catalog.Warnings);
        Assert.Contains(catalog.Themes, theme => theme.Name == "One Dark");
    }

    [Fact]
    public async Task ImportedFamiliesAndInstalledExtensionsAreDiscoverableWithoutOverwritingFiles()
    {
        var installed = Path.Combine(root, "installed");
        var extension = Path.Combine(installed, "example", "themes");
        Directory.CreateDirectory(extension);
        var path = Path.Combine(extension, "family.json");
        await File.WriteAllTextAsync(path, """{"themes":[{"name":"Extension Dark","appearance":"dark","style":{}},{"name":"Extension Light","appearance":"light","style":{}}]}""", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(installed, "example", "manifest.json"), "{}", TestContext.Current.CancellationToken);
        var catalog = new ThemeCatalog([installed]);
        Assert.Empty(catalog.Warnings);
        Assert.Contains(catalog.Themes, theme => theme.Name == "Extension Light");
        var imported = Path.Combine(root, "imported");
        Assert.Equal("Extension Dark", await ThemeCatalog.ImportAsync(path, imported));
        await ThemeCatalog.ImportAsync(path, imported);
        Assert.Equal(2, Directory.GetFiles(imported).Length);
        Assert.All(Directory.GetFiles(imported), file => Assert.Equal(2, ThemeCatalog.ReadFamily(file).Count));
    }

    [Theory]
    [InlineData("{\n// My preferences\n\"theme\": {\"mode\":\"system\",\"dark\":\"One Dark\"},\n\"lsp\":{\"theme\":\"nested\"},\n}")]
    [InlineData("{/* café */\"lsp\":{\"theme\":\"nested\"}}")]
    [InlineData("{\"theme\":\"Old\",\"theme\":\"Duplicate\",\"lsp\":{\"theme\":\"nested\"}}")]
    public void SavingThemePreservesCommentsAndUnrelatedConfiguration(string original)
    {
        var updated = ThemeCatalog.ReplaceThemeSetting(original, "Ayu \"Light\"");
        using var parsed = System.Text.Json.JsonDocument.Parse(updated, new() { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        Assert.Equal("Ayu \"Light\"", parsed.RootElement.GetProperty("theme").GetString());
        Assert.Contains("\"lsp\":{\"theme\":\"nested\"}", updated);
        if (original.Contains("// My preferences")) Assert.Contains("// My preferences", updated);
        if (original.Contains("/* café */")) Assert.Contains("/* café */", updated);
    }

    [Fact]
    public async Task SelectionPersistsAndInvalidSettingsAreNeverOverwritten()
    {
        var path = Path.Combine(root, "settings.json");
        await ThemeCatalog.SaveSelectionAsync("Gruvbox Dark", path);
        Assert.Contains("Gruvbox Dark", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(path, "{ invalid", TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => ThemeCatalog.SaveSelectionAsync("One Light", path));
        Assert.Equal("{ invalid", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task PickerPreviewsLightAndDarkAndCancellationRestoresResources()
    {
        var application = (App)Avalonia.Application.Current!;
        var original = application.EditorTheme;
        var owner = new MainWindow();
        owner.Show();
        try
        {
            var task = Dialogs.ChooseThemeAsync(owner, ZedTheme.BuiltInThemes(), original.Name, application.PreviewTheme);
            var dialog = Assert.Single(owner.OwnedWindows);
            var search = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<Avalonia.Controls.TextBox>().Single();
            search.Text = "One Light";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("One Light", application.EditorTheme.Name);
            Assert.Equal(Color.Parse("#fafafa"), ((ISolidColorBrush)application.Resources["zed.editor.background"]!).Color);
            search.Text = "One Dark";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("One Dark", application.EditorTheme.Name);
            dialog.Close();
            Assert.Null(await task);
            application.CancelThemePreview();
            Assert.Equal(original.Name, application.EditorTheme.Name);
        }
        finally { application.CancelThemePreview(); owner.Close(); }
    }

    public void Dispose() => Directory.Delete(root, true);
}

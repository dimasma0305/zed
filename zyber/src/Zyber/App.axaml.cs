using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Zyber.Services;

namespace Zyber;

public sealed partial class App : Application
{
    public ZedTheme EditorTheme { get; private set; } = ZedTheme.BuiltInThemes().Single(theme => theme.Name == "One Dark");
    public ThemeCatalog Themes { get; private set; } = new();
    private ZedTheme? previewTheme;
    public string? ThemeWarning { get; private set; }
    public event EventHandler? EditorThemeChanged;
    private (string Mode, string Dark, string Light, string? Warning) preferences;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        preferences = ZedTheme.ReadThemePreference();
        ThemeWarning = preferences.Warning;
        RequestedThemeVariant = preferences.Mode switch { "dark" => ThemeVariant.Dark, "light" => ThemeVariant.Light, _ => ThemeVariant.Default };
        if (preferences.Mode == "explicit")
        {
            var selected = Themes.Themes.FirstOrDefault(theme => theme.Name == preferences.Dark);
            if (selected is not null) RequestedThemeVariant = selected.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        }
        ApplyTheme();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        var name = ActualThemeVariant == ThemeVariant.Light ? preferences.Light : preferences.Dark;
        var theme = previewTheme ?? Themes.Themes.FirstOrDefault(theme => theme.Name == name);
        if (theme is null)
        {
            ThemeWarning = $"Theme '{name}' could not be found. Using Zed's default theme.";
            theme = ZedTheme.BuiltInThemes().Single(theme => theme.Name == (ActualThemeVariant == ThemeVariant.Light ? "One Light" : "One Dark"));
        }
        EditorTheme = theme;
        theme.Apply(this);
        EditorThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReloadThemesAsync() => Themes = await Task.Run(() => new ThemeCatalog());

    public void PreviewTheme(ZedTheme theme)
    {
        previewTheme = theme;
        RequestedThemeVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyTheme();
    }

    public void CancelThemePreview()
    {
        previewTheme = null;
        RequestedThemeVariant = preferences.Mode switch
        {
            "dark" => ThemeVariant.Dark, "light" => ThemeVariant.Light,
            "explicit" => Themes.Themes.FirstOrDefault(theme => theme.Name == preferences.Dark)?.IsLight == true ? ThemeVariant.Light : ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        ApplyTheme();
    }

    public async Task SelectThemeAsync(ZedTheme theme)
    {
        await ThemeCatalog.SaveSelectionAsync(theme.Name);
        preferences = ("explicit", theme.Name, theme.Name, null);
        ThemeWarning = theme.Warnings.Count > 0 ? string.Join("\n", theme.Warnings) : null;
        previewTheme = null;
        RequestedThemeVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyTheme();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(desktop.Args ?? [], new RecentProjects(RecentProjects.DefaultStoragePath));
            if (desktop.Args is null || desktop.Args.Length == 0) window.NewDocument();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}

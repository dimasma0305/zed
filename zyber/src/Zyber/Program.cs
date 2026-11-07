using Avalonia;

namespace Zyber;

internal static class Program
{
    [STAThread]
    public static void Main(string[] arguments) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(arguments);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "avares://Zyber/Assets#IBM Plex Sans" })
        .LogToTrace();
}

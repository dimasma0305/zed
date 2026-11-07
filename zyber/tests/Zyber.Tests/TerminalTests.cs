using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Zyber.Views;
using Xunit;

namespace Zyber.Tests;

public sealed class TerminalTests
{
    [AvaloniaFact]
    public async Task IntegratedTerminalRunsCommandsThroughAPseudoTerminal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zyber-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "output.txt");
        using var dock = new TerminalDock { StartingDirectory = directory };
        var window = new Window { Content = dock, Width = 800, Height = 400 };
        try
        {
            window.Show();
            var terminal = dock.AddSession(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash",
                OperatingSystem.IsWindows() ? ["-NoLogo", "-NoProfile"] : ["--noprofile", "--norc"],
                new Dictionary<string, string> { ["ZYBER_PTY_TEST_PATH"] = path });
            for (var attempt = 0; attempt < 200 && !terminal.IsLive; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            Assert.True(terminal.IsLive, "The shell failed to start in the terminal control.");
            var theme = Assert.IsType<App>(Avalonia.Application.Current).EditorTheme;
            var palette = new XTerm.Common.ColorPalette(terminal.Options!.Theme);
            static int Rgb(Avalonia.Media.Color color) => (color.R << 16) | (color.G << 8) | color.B;
            Assert.Equal(Rgb(theme.Color("terminal.ansi.bright_green")), palette[10]);
            Assert.Equal(Rgb(theme.Color("terminal.background")), palette.Background);
            Assert.Equal(Rgb(theme.Color("terminal.foreground")), palette.Foreground);
            await terminal.SendInputAsync(OperatingSystem.IsWindows()
                ? "Set-Content -LiteralPath $env:ZYBER_PTY_TEST_PATH -Value 'pty-ok'\r"
                : "printf 'pty-ok' > \"$ZYBER_PTY_TEST_PATH\"\r", TestContext.Current.CancellationToken);
            for (var attempt = 0; attempt < 200 && !File.Exists(path); attempt++)
                await Task.Delay(25, TestContext.Current.CancellationToken);
            Assert.Equal("pty-ok", (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Trim());
            dock.IsVisible = false;
            Assert.True(terminal.IsLive);
            dock.IsVisible = true;
            Assert.Same(terminal, dock.ActiveTerminal);
        }
        finally { window.Close(); dock.Dispose(); Directory.Delete(directory, true); }
    }
}

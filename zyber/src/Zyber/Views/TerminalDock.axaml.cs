using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using Zyber.Models;
using Zyber.Services;
using XTerm.Options;

namespace Zyber.Views;

public sealed class TerminalSession(TerminalControl terminal, string title) : ObservableState
{
    public TerminalControl Terminal { get; } = terminal;
    private string currentTitle = title;
    public string Title { get => currentTitle; set { currentTitle = value; Notify(); } }
}

public sealed partial class TerminalDock : UserControl, IDisposable
{
    public ObservableCollection<TerminalSession> Items { get; } = [];
    public string StartingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public event EventHandler? HideRequested;
    public TerminalControl? ActiveTerminal => (Sessions.SelectedItem as TerminalSession)?.Terminal;

    public TerminalDock()
    {
        InitializeComponent();
        Sessions.ItemsSource = Items;
        if (Application.Current is App app) app.EditorThemeChanged += ThemeChanged;
    }

    public TerminalControl AddSession(string? process = null, string[]? arguments = null, IDictionary<string, string>? environment = null)
    {
        process ??= OperatingSystem.IsWindows() ? "powershell.exe" : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        var terminal = new TerminalControl
        {
            Process = process, ProcessArgs = arguments ?? [], StartingDirectory = StartingDirectory,
            EnvironmentVariables = environment, FontFamily = new FontFamily("avares://Zyber/Assets#Lilex"), FontSize = 15,
            BufferSize = 10_000, AllowWindowOps = false,
            CursorStyle = XTerm.Common.CursorStyle.Block, CursorBlink = true
        };
        if (Application.Current is App app) ApplyTheme(terminal, app.EditorTheme);
        terminal.Loaded += (_, _) => { if (Application.Current is App current) ApplyTheme(terminal, current.EditorTheme); };
        var session = new TerminalSession(terminal, Environment.UserName + " — " + Path.GetFileName(process));
        terminal.ProcessExited += (_, _) => Dispatcher.UIThread.Post(() => session.Title += " (exited)");
        Items.Add(session);
        Terminals.Children.Add(terminal);
        Sessions.SelectedItem = session;
        Dispatcher.UIThread.Post(() => terminal.Focus(), DispatcherPriority.Input);
        return terminal;
    }

    public static void ApplyTheme(TerminalControl terminal, ZedTheme theme)
    {
        // XTerm's color parser accepts RGB, while Zed's source stores RGBA.
        // Selection opacity stays on the Avalonia brush above the terminal renderer.
        static string Rgb(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        string Token(string key) => Rgb(theme.Color(key));
        terminal.Background = theme.Brush("terminal.background");
        terminal.Foreground = theme.Brush("terminal.foreground");
        terminal.SelectionBrush = new SolidColorBrush(theme.PlayerColor("selection"));
        terminal.CursorColor = theme.PlayerColor("cursor");
        terminal.BoldColor = theme.Color("terminal.bright_foreground");
        terminal.SearchHighlightBrush = theme.Brush("search.match_background");
        terminal.SearchCurrentBrush = theme.Brush("search.active_match_background");
        // The emulator replaces its initial options with a live snapshot when loaded.
        var options = terminal.Options ?? new TerminalOptions();
        options.Scrollback = 10_000;
        options.MinimumContrastRatio = 1;
        options.Theme = new ThemeOptions
            {
                Background = Token("terminal.background"), Foreground = Token("terminal.foreground"),
                Cursor = Rgb(theme.PlayerColor("cursor")),
                Bold = Token("terminal.bright_foreground"),
                Black = Token("terminal.ansi.black"), Red = Token("terminal.ansi.red"),
                Green = Token("terminal.ansi.green"), Yellow = Token("terminal.ansi.yellow"),
                Blue = Token("terminal.ansi.blue"), Magenta = Token("terminal.ansi.magenta"),
                Cyan = Token("terminal.ansi.cyan"), White = Token("terminal.ansi.white"),
                BrightBlack = Token("terminal.ansi.bright_black"), BrightRed = Token("terminal.ansi.bright_red"),
                BrightGreen = Token("terminal.ansi.bright_green"), BrightYellow = Token("terminal.ansi.bright_yellow"),
                BrightBlue = Token("terminal.ansi.bright_blue"), BrightMagenta = Token("terminal.ansi.bright_magenta"),
                BrightCyan = Token("terminal.ansi.bright_cyan"), BrightWhite = Token("terminal.ansi.bright_white")
            };
        terminal.Options = options;
    }

    private void ThemeChanged(object? sender, EventArgs arguments)
    {
        if (Application.Current is App app)
            foreach (var session in Items) ApplyTheme(session.Terminal, app.EditorTheme);
    }
    private void SessionSelected(object? sender, SelectionChangedEventArgs arguments)
    {
        foreach (var session in Items) session.Terminal.IsVisible = session == Sessions.SelectedItem;
        Dispatcher.UIThread.Post(() => ActiveTerminal?.Focus(), DispatcherPriority.Input);
    }
    private void NewSessionClicked(object? sender, RoutedEventArgs arguments) => AddSession();
    private void HideClicked(object? sender, RoutedEventArgs arguments) => HideRequested?.Invoke(this, EventArgs.Empty);
    private void CloseSessionClicked(object? sender, RoutedEventArgs arguments)
    {
        arguments.Handled = true;
        if (sender is not Button { DataContext: TerminalSession session }) return;
        var index = Items.IndexOf(session);
        session.Terminal.Dispose();
        Terminals.Children.Remove(session.Terminal);
        Items.Remove(session);
        Sessions.SelectedItem = Items.Count > 0 ? Items[Math.Min(index, Items.Count - 1)] : null;
        if (Items.Count == 0) HideRequested?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        if (Application.Current is App app) app.EditorThemeChanged -= ThemeChanged;
        foreach (var session in Items) session.Terminal.Dispose();
        Items.Clear();
        Terminals.Children.Clear();
    }
}

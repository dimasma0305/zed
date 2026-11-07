using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Zyber.Services;
using System.Text.RegularExpressions;

namespace Zyber;

public enum UnsavedChoice { Cancel, Discard, Save }
public sealed record EditorCommand(string Label, string Shortcut, Func<Task> Execute);

public sealed record FileLocation(ProjectFile File, int? Line, int? Column);

public static class Dialogs
{
    public static async Task ShowErrorAsync(Window owner, string message)
    {
        var dialog = CreateDialog("Zyber", 460);
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close } };
        await dialog.ShowDialog(owner);
    }

    public static Task<UnsavedChoice> ConfirmUnsavedAsync(Window owner, string name)
    {
        var dialog = CreateDialog("Unsaved changes", 480);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, choice) in new[] { ("Cancel", UnsavedChoice.Cancel), ("Discard", UnsavedChoice.Discard), ("Save", UnsavedChoice.Save) })
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => dialog.Close(choice);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20,
            Children = { new TextBlock { Text = $"Save changes to {name}?", TextWrapping = TextWrapping.Wrap }, buttons } };
        return dialog.ShowDialog<UnsavedChoice>(owner);
    }

    public static Task<bool> ConfirmReplaceAsync(Window owner, string path)
    {
        var dialog = CreateDialog("Replace file?", 480);
        var cancel = new Button { Content = "Cancel" };
        var replace = new Button { Content = "Replace" };
        cancel.Click += (_, _) => dialog.Close(false);
        replace.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20,
            Children = {
                new TextBlock { Text = $"Replace {System.IO.Path.GetFileName(path)} with this document?", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, replace } } } };
        return dialog.ShowDialog<bool>(owner);
    }

    public static Task<EditorCommand?> ChooseCommandAsync(Window owner, IReadOnlyList<EditorCommand> commands)
    {
        var dialog = CreateDialog("Command palette", 600);
        dialog.Height = 440;
        dialog.SizeToContent = SizeToContent.Manual;
        var input = new TextBox { PlaceholderText = "Type a command…", Margin = new Thickness(12) };
        var list = new ListBox { Margin = new Thickness(6, 0, 6, 12) };
        List<EditorCommand> matching = [];
        void Filter()
        {
            matching = commands.Where(command => command.Label.Contains(input.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
            list.ItemsSource = matching.Select(command => command.Label + "     " + command.Shortcut).ToList();
            list.SelectedIndex = matching.Count > 0 ? 0 : -1;
        }
        void Choose()
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < matching.Count) dialog.Close(matching[list.SelectedIndex]);
        }
        input.TextChanged += (_, _) => Filter();
        list.DoubleTapped += (_, _) => Choose();
        dialog.KeyDown += (_, arguments) =>
        {
            switch (arguments.Key)
            {
                case Key.Escape: dialog.Close(null); arguments.Handled = true; break;
                case Key.Enter: Choose(); arguments.Handled = true; break;
                case Key.Down: list.SelectedIndex = Math.Min(list.SelectedIndex + 1, matching.Count - 1); arguments.Handled = true; break;
                case Key.Up: list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); arguments.Handled = true; break;
            }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(input);
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        dialog.Content = layout;
        dialog.Opened += (_, _) => input.Focus();
        Filter();
        return dialog.ShowDialog<EditorCommand?>(owner);
    }

    public static Task<ZedTheme?> ChooseThemeAsync(Window owner, IReadOnlyList<ZedTheme> themes, string selectedName, Action<ZedTheme> preview)
    {
        var dialog = CreateDialog("Theme selector", 560);
        dialog.Name = "ThemeSelector";
        dialog.Height = 480;
        dialog.SizeToContent = SizeToContent.Manual;
        var input = new TextBox { Name = "ThemeSearch", PlaceholderText = "Search themes…", Margin = new Thickness(12) };
        var list = new ListBox { Name = "ThemeList", Margin = new Thickness(6, 0, 6, 12) };
        List<ZedTheme> matching = [];
        void Filter()
        {
            matching = themes.Where(theme => theme.Name.Contains(input.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
            list.ItemsSource = matching.Select(theme => theme.Name + (theme.IsLight ? "   ·   Light" : "   ·   Dark")).ToArray();
            list.SelectedIndex = matching.Count > 0 ? Math.Max(0, matching.FindIndex(theme => theme.Name == selectedName)) : -1;
        }
        void Choose()
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < matching.Count) dialog.Close(matching[list.SelectedIndex]);
        }
        input.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < matching.Count) preview(matching[list.SelectedIndex]);
        };
        list.DoubleTapped += (_, _) => Choose();
        dialog.KeyDown += (_, arguments) =>
        {
            switch (arguments.Key)
            {
                case Key.Escape: dialog.Close(null); arguments.Handled = true; break;
                case Key.Enter: Choose(); arguments.Handled = true; break;
                case Key.Down: list.SelectedIndex = Math.Min(list.SelectedIndex + 1, matching.Count - 1); arguments.Handled = true; break;
                case Key.Up: list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); arguments.Handled = true; break;
            }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(input);
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        var hint = new TextBlock { Text = "↑ ↓ Preview    Enter Apply    Esc Cancel", Margin = new Thickness(12), FontSize = 13 };
        Grid.SetRow(hint, 2);
        layout.Children.Add(hint);
        dialog.Content = layout;
        dialog.Opened += (_, _) => input.Focus();
        Filter();
        return dialog.ShowDialog<ZedTheme?>(owner);
    }

    public static async Task<FileLocation?> ChooseFileAsync(Window owner, ProjectFileIndex index)
    {
        var dialog = CreateDialog("Find file", 640);
        dialog.Name = "FileFinder";
        dialog.Height = 430;
        dialog.SizeToContent = SizeToContent.Manual;
        var query = new TextBox { Name = "FileQuery", PlaceholderText = "Search project files…", MaxLength = 256, Margin = new Thickness(12) };
        var results = new ListBox { Name = "FileResults", Margin = new Thickness(4) };
        var status = new TextBlock { Margin = new Thickness(12, 4), FontSize = 12 };
        IReadOnlyList<ProjectFile> matching = [];
        CancellationTokenSource? pending = null;
        var closed = false;
        (string Query, int? Line, int? Column) Parse()
        {
            var text = query.Text ?? "";
            var location = Regex.Match(text, @"^(.*?):([1-9]\d*)(?::([1-9]\d*))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            if (!location.Success || !int.TryParse(location.Groups[2].Value, out var line)) return (text, null, null);
            int? column = int.TryParse(location.Groups[3].Value, out var parsedColumn) ? parsedColumn : null;
            return (location.Groups[1].Value, line, column);
        }
        async void Filter()
        {
            pending?.Cancel();
            var request = new CancellationTokenSource();
            pending = request;
            matching = [];
            results.ItemsSource = Array.Empty<string>();
            status.Text = "Searching…";
            var token = request.Token;
            var text = Parse().Query;
            try
            {
                var ranked = await Task.Run(() => FuzzyMatcher.Rank(index.Files, text, cancellationToken: token), token);
                if (closed || pending != request) return;
                matching = ranked;
                results.ItemsSource = matching.Select(file => file.RelativePath).ToArray();
                results.SelectedIndex = matching.Count > 0 ? 0 : -1;
                status.Text = $"{matching.Count} results · {index.Files.Count} indexed files" + (index.LimitReached ? " · index limit reached" : "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { if (pending == request) pending = null; request.Dispose(); }
        }
        void Choose()
        {
            if (results.SelectedIndex < 0 || results.SelectedIndex >= matching.Count) return;
            var location = Parse();
            dialog.Close(new FileLocation(matching[results.SelectedIndex], location.Line, location.Column));
        }
        query.TextChanged += (_, _) => Filter();
        results.DoubleTapped += (_, _) => Choose();
        dialog.KeyDown += (_, arguments) =>
        {
            switch (arguments.Key)
            {
                case Key.Escape: dialog.Close(null); arguments.Handled = true; break;
                case Key.Enter: Choose(); arguments.Handled = true; break;
                case Key.Down: results.SelectedIndex = Math.Min(results.SelectedIndex + 1, matching.Count - 1); arguments.Handled = true; break;
                case Key.Up: results.SelectedIndex = Math.Max(0, results.SelectedIndex - 1); arguments.Handled = true; break;
            }
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(query);
        Grid.SetRow(results, 1); grid.Children.Add(results);
        Grid.SetRow(status, 2); grid.Children.Add(status);
        dialog.Content = grid;
        dialog.Opened += (_, _) => { query.Focus(); Filter(); };
        dialog.Closed += (_, _) => { closed = true; pending?.Cancel(); };
        return await dialog.ShowDialog<FileLocation?>(owner);
    }

    public static Task<string?> PromptAsync(Window owner, string title, string label, string initial = "")
    {
        var dialog = CreateDialog(title, 520);
        var input = new TextBox { Text = initial, PlaceholderText = label };
        var accept = new Button { Content = "Continue" };
        var cancel = new Button { Content = "Cancel" };
        accept.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.Close(input.Text.Trim()); };
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children =
        {
            new TextBlock { Text = label }, input,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } }
        }};
        dialog.Opened += (_, _) => input.Focus();
        return dialog.ShowDialog<string?>(owner);
    }

    private static Window CreateDialog(string title, double width) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
    };
}

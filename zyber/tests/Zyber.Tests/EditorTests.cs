using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Zyber.Models;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Zyber.Tests.TestAppBuilder))]

namespace Zyber.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia().With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "avares://Zyber/Assets#IBM Plex Sans" });
}

public sealed class EditorTests
{
    [AvaloniaFact]
    public void UndoReturnsToTheSavedState()
    {
        var document = new EditorTab();
        document.Document.Insert(0, "first edit");
        Assert.True(document.IsDirty);
        document.Document.UndoStack.Undo();
        Assert.False(document.IsDirty);
    }

    [AvaloniaFact]
    public void EditsMadeDuringSaveRemainDirty()
    {
        var document = new EditorTab();
        document.Document.Insert(0, "saved version");
        var saved = document.Document.Text;
        document.Document.Insert(document.Document.TextLength, "newer edit");
        document.RecordSave("test.cs", "fingerprint", saved);
        Assert.True(document.IsDirty);
    }

    [AvaloniaFact]
    public void TypingAndTabSwitchingPreserveIndependentUndoHistories()
    {
        var window = new MainWindow();
        window.Show();
        window.NewDocument();
        var first = window.State.ActiveDocument!;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("first document");
        Assert.Equal("first document", first.Document.Text);
        window.NewDocument();
        var second = window.State.ActiveDocument!;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("second document");
        window.State.ActiveTab = first;
        first.Document.UndoStack.Undo();
        Assert.Equal("", first.Document.Text);
        Assert.Equal("second document", second.Document.Text);
        second.Document.UndoStack.Undo();
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlSSavesTheActiveDocument()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zyber-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.cs");
        await File.WriteAllTextAsync(path, "// original\n", TestContext.Current.CancellationToken);
        var window = new MainWindow();
        try
        {
            window.Show();
            await window.OpenDocumentAsync(path);
            await window.OpenDocumentAsync(path);
            Assert.Single(window.State.Tabs);
            var document = window.State.ActiveDocument!;
            document.Document.Insert(document.Document.TextLength, "// changed\n");
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            window.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            for (var attempt = 0; attempt < 200 && document.IsDirty; attempt++) await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.False(document.IsDirty);
            Assert.Equal("// original\n// changed\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal("C#", window.State.Language);
        }
        finally
        {
            foreach (var tab in window.State.Tabs.OfType<EditorTab>()) tab.Document.UndoStack.MarkAsOriginalFile();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void ClosingADirtyWindowOffersCancelAndPreservesText()
    {
        var window = new MainWindow();
        window.Show();
        window.NewDocument();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("do not lose this");
        window.Close();
        Assert.True(window.IsVisible);
        var dialog = Assert.Single(window.OwnedWindows);
        var cancel = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"));
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("do not lose this", window.State.ActiveDocument!.Document.Text);
        window.State.ActiveDocument!.Document.UndoStack.MarkAsOriginalFile();
        window.Close();
    }

    [AvaloniaFact]
    public void DiffTabsAreReadOnlyAndKeepEditingBuffersIndependent()
    {
        var window = new MainWindow();
        window.Show();
        window.NewDocument();
        var original = window.State.ActiveDocument!;
        original.Document.Text = "my unsaved text";
        window.OpenDiff(new("test.txt (working tree)", "--- a/test.txt\n+++ b/test.txt\n-old\n+new\n"));
        var editor = window.FindControl<TextEditor>("Editor")!;
        Assert.True(editor.IsVisible);
        Assert.True(editor.IsReadOnly);
        Assert.Contains("+new", editor.Text);
        window.State.ActiveTab = original;
        Assert.False(editor.IsReadOnly);
        Assert.Equal("my unsaved text", editor.Text);
        Assert.True(original.IsDirty);
        original.Document.UndoStack.MarkAsOriginalFile();
        window.Close();
    }

    [AvaloniaFact]
    public async Task RealTypeScriptFormattingIsOneUndoableEdit()
    {
        var script = Environment.GetEnvironmentVariable("ZYBER_TEST_TYPESCRIPT");
        if (string.IsNullOrEmpty(script)) Assert.Skip("TypeScript integration server is not configured.");
        var directory = Path.Combine(Path.GetTempPath(), "zyber-format-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.ts");
        const string source = "const value={name:\"Zyber\",count:1};\n";
        await File.WriteAllTextAsync(path, source, TestContext.Current.CancellationToken);
        var configuration = new Zyber.Languages.LanguageServerConfiguration("typescript-language-server", Zyber.Languages.LanguageServerRegistry.FindExecutable("node")!, new[] { script!, "--stdio" });
        var window = new MainWindow([], languageServers: language => language.Id == "typescript" ? configuration : null);
        try
        {
            window.Show();
            await window.OpenFolderAsync(directory);
            await window.OpenDocumentAsync(path);
            await window.Code.FormatAsync();
            var document = window.State.ActiveDocument!.Document;
            Assert.NotEqual(source, document.Text);
            Assert.Contains("name: \"Zyber\"", document.Text);
            Assert.True(window.State.ActiveDocument.IsDirty);
            document.UndoStack.Undo();
            Assert.Equal(source, document.Text);
            Assert.False(window.State.ActiveDocument.IsDirty);
            await window.Code.RestartAsync();
            await window.Code.FormatAsync();
            Assert.NotEqual(source, document.Text);
        }
        finally
        {
            foreach (var tab in window.State.Tabs.OfType<EditorTab>()) tab.Document.UndoStack.MarkAsOriginalFile();
            await window.Code.DisposeAsync();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task RealLanguageServerDrivesDiagnosticsAndKeyboardCompletion()
    {
        var script = Environment.GetEnvironmentVariable("ZYBER_TEST_PYRIGHT");
        if (string.IsNullOrEmpty(script)) Assert.Skip("Pyright integration server is not configured.");
        var directory = Path.Combine(Path.GetTempPath(), "zyber-language-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.py");
        const string source = "def greet(name: str) -> str:\n    return 'hello ' + name\n\nnumber: int = 'wrong'\nresult = greet('Zyber')\nresult.\n";
        await File.WriteAllTextAsync(path, source, TestContext.Current.CancellationToken);
        var configuration = new Zyber.Languages.LanguageServerConfiguration("pyright", Zyber.Languages.LanguageServerRegistry.FindExecutable("node")!, new[] { script!, "--stdio" });
        var window = new MainWindow([], languageServers: language => language.Id == "python" ? configuration : null);
        try
        {
            window.Show();
            await window.OpenFolderAsync(directory);
            await window.OpenDocumentAsync(path);
            await window.Code.SynchronizeAllAsync();
            for (var attempt = 0; attempt < 400 && window.Code.Diagnostics.Items.Count == 0; attempt++)
                await Task.Delay(25, TestContext.Current.CancellationToken);
            Assert.Contains(window.Code.Diagnostics.Items, item => item.Diagnostic.Range.Start.Line == 3);
            var editor = window.FindControl<TextEditor>("Editor")!;
            editor.CaretOffset = source.LastIndexOf("result.", StringComparison.Ordinal) + 7;
            editor.TextArea.Focus();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await window.Code.ShowCompletionsAsync();
            Assert.Contains(window.Code.LastCompletions, item => item.Label == "upper");
            window.KeyTextInput("upp");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Assert.Contains("result.upper\n", window.State.ActiveDocument!.Document.Text);
            window.State.ActiveDocument.Document.UndoStack.Undo();
            Assert.Contains("result.upp\n", window.State.ActiveDocument.Document.Text);
            await window.Code.ShowDiagnosticsAsync();
            Assert.True(window.FindControl<Zyber.Views.DiagnosticsView>("DiagnosticsPanel")!.IsVisible);
        }
        finally
        {
            foreach (var tab in window.State.Tabs.OfType<EditorTab>()) tab.Document.UndoStack.MarkAsOriginalFile();
            await window.Code.DisposeAsync();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task CtrlPFinderOpensAFileAtTheRequestedLineAndColumn()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zyber-finder-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "MainWindow.cs");
        await File.WriteAllTextAsync(path, "first\nsecond\n", TestContext.Current.CancellationToken);
        var window = new MainWindow();
        try
        {
            window.Show();
            await window.OpenFolderAsync(directory);
            window.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.P, "p");
            window.KeyRelease(Key.P, RawInputModifiers.Control, PhysicalKey.P, "p");
            for (var attempt = 0; attempt < 200 && !window.OwnedWindows.Any(); attempt++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            var picker = Assert.Single(window.OwnedWindows);
            picker.GetVisualDescendants().OfType<TextBox>().Single().Text = "mw:2:3";
            var results = picker.GetVisualDescendants().OfType<ListBox>().Single();
            for (var attempt = 0; attempt < 200 && results.ItemCount != 1; attempt++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Equal(1, results.ItemCount);
            picker.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            for (var attempt = 0; attempt < 200 && window.State.ActiveDocument is null; attempt++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Equal(path, window.State.ActiveDocument!.FilePath);
            Assert.Equal(8, window.FindControl<TextEditor>("Editor")!.CaretOffset);
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task ProjectSearchOpensTheSelectedMatchAndPreservesOtherBuffers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zyber-search-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.cs");
        await File.WriteAllTextAsync(path, "// first\n// needle\n", TestContext.Current.CancellationToken);
        var window = new MainWindow();
        try
        {
            window.Show();
            await window.OpenFolderAsync(directory);
            window.NewDocument();
            var original = window.State.ActiveDocument!;
            original.Document.Insert(0, "keep my edits");
            await window.ShowProjectSearchAsync();
            var search = window.State.ActiveSearch!;
            search.Query = "needle";
            await search.SearchAsync();
            Assert.True(window.FindControl<Zyber.Views.ProjectSearchView>("ProjectSearchPanel")!.IsVisible);
            await window.OpenSearchResultAsync(Assert.Single(search.Results));
            Assert.Equal(path, window.State.ActiveDocument!.FilePath);
            Assert.Equal("needle", window.FindControl<TextEditor>("Editor")!.SelectedText);
            window.State.ActiveTab = original;
            Assert.Equal("keep my edits", window.FindControl<TextEditor>("Editor")!.Text);
            Assert.True(original.IsDirty);
            window.State.ActiveTab = search;
            Assert.Single(search.Results);
        }
        finally
        {
            foreach (var tab in window.State.Tabs.OfType<EditorTab>()) tab.Document.UndoStack.MarkAsOriginalFile();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task FileWatcherReloadsCleanBuffersAndPreservesConflictingEdits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zyber-watch-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.cs");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        var window = new MainWindow();
        try
        {
            window.Show();
            await window.OpenFolderAsync(directory);
            await window.OpenDocumentAsync(path);
            var document = window.State.ActiveDocument!;
            await File.WriteAllTextAsync(path, "external edit", TestContext.Current.CancellationToken);
            for (var attempt = 0; attempt < 200 && document.Document.Text != "external edit"; attempt++)
                await Task.Delay(25, TestContext.Current.CancellationToken);
            Assert.Equal("external edit", document.Document.Text);
            Assert.False(document.IsDirty);
            document.Document.Insert(document.Document.TextLength, " plus mine");
            await File.WriteAllTextAsync(path, "conflicting edit", TestContext.Current.CancellationToken);
            await window.RefreshFilesFromDiskAsync();
            Assert.Equal("external edit plus mine", document.Document.Text);
            Assert.True(document.IsDirty);
            Assert.True(document.HasExternalChanges);
            File.Delete(path);
            await window.RefreshFilesFromDiskAsync();
            Assert.Equal("external edit plus mine", document.Document.Text);
        }
        finally
        {
            foreach (var tab in window.State.Tabs.OfType<EditorTab>()) tab.Document.UndoStack.MarkAsOriginalFile();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void WindowRendersWithARealEditorControl()
    {
        var window = new MainWindow();
        window.Show();
        window.NewDocument();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("// Welcome to Zyber\nConsole.WriteLine(\"Build something good.\");\n");
        var editor = window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        Assert.Equal(15 * 1.618, editor.TextArea.TextView.DefaultLineHeight, precision: 3);
        var application = Assert.IsType<App>(Application.Current);
        Assert.Equal(application.EditorTheme.Color("editor.background"), Assert.IsType<Avalonia.Media.SolidColorBrush>(editor.Background).Color);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("ZYBER_SCREENSHOT_PATH");
        if (!string.IsNullOrWhiteSpace(output)) frame.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.State.ActiveDocument!.Document.UndoStack.MarkAsOriginalFile();
        window.Close();
    }
}

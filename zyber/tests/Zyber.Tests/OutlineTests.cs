using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit;
using Zyber.Languages;
using Zyber.Services;
using Zyber.Views;
using Xunit;

namespace Zyber.Tests;

public sealed class OutlineTests
{
    [Fact]
    public void JsonSymbolsPreserveHierarchyEscapesAndUtf16Offsets()
    {
        const string source = "{/* 😀 comment */\r\n  \"café\": {\"a\\\"b\": true}, \"items\": [{\"😀\": 1}],\n}";
        var result = DocumentOutline.Json(source, TestContext.Current.CancellationToken);
        Assert.Null(result.Message);
        Assert.Equal(new[] { "café", "items" }, result.Symbols.Select(symbol => symbol.Name));
        var escaped = Assert.Single(result.Symbols[0].Children);
        Assert.Equal("a\"b", escaped.Name);
        Assert.Equal("a\\\"b", source[escaped.SelectionStart..escaped.SelectionEnd]);
        var emoji = Assert.Single(result.Symbols[1].Children);
        Assert.Equal("😀", source[emoji.SelectionStart..emoji.SelectionEnd]);
        Assert.Contains("true", source[escaped.Start..escaped.End]);
        Assert.True(result.Symbols[0].Start < escaped.Start && result.Symbols[0].End > escaped.End);
    }

    [Theory]
    [InlineData("{\"bad\":")]
    [InlineData("{} {}")]
    [InlineData("[1,2")]
    public void IncompleteOrInvalidJsonReportsTheProblemWithoutPublishingMisleadingSymbols(string text)
    {
        var result = DocumentOutline.Json(text, TestContext.Current.CancellationToken);
        Assert.Empty(result.Symbols);
        Assert.StartsWith("JSON outline:", result.Message);
    }

    [Fact]
    public void JsonSymbolLimitsAreReported()
    {
        var text = "{" + string.Join(',', Enumerable.Range(0, DocumentOutline.MaximumSymbols + 1).Select(index => $"\"key{index}\":0")) + "}";
        Assert.Contains("limited", DocumentOutline.Json(text, TestContext.Current.CancellationToken).Message);
    }

    [Fact]
    public void HierarchicalLspSymbolsUseSelectionRangesAndFlatSymbolsExcludeOtherFiles()
    {
        var path = Path.GetFullPath("sample.py");
        const string text = "class Example:\n  def method(): pass\n";
        using var hierarchy = JsonDocument.Parse("""[{"name":"Example","kind":5,"range":{"start":{"line":0,"character":0},"end":{"line":2,"character":0}},"selectionRange":{"start":{"line":0,"character":6},"end":{"line":0,"character":13}},"children":[{"name":"method","kind":6,"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":20}},"selectionRange":{"start":{"line":1,"character":6},"end":{"line":1,"character":12}}}]}]""");
        var result = DocumentOutline.FromLanguageServer(text, path, hierarchy.RootElement);
        var symbol = Assert.Single(result.Symbols);
        Assert.Equal("Example", text[symbol.SelectionStart..symbol.SelectionEnd]);
        var method = Assert.Single(symbol.Children);
        Assert.Equal("method", text[method.SelectionStart..method.SelectionEnd]);
        var range = new LspRange(new(0, 0), new(0, 13));
        using var flat = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            new { name = "this", kind = 5, location = new { uri = LspText.FileUri(path), range } },
            new { name = "elsewhere", kind = 5, location = new { uri = LspText.FileUri(path + ".other"), range } }
        }));
        Assert.Equal("this", Assert.Single(DocumentOutline.FromLanguageServer(text, path, flat.RootElement).Symbols).Name);
    }

    [AvaloniaFact]
    public async Task OutlineSearchNavigationRefreshAndTabChangesKeepPositionsCurrent()
    {
        var root = Path.Combine(Path.GetTempPath(), "zyber-outline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "sample.json");
        await File.WriteAllTextAsync(path, "{\"outer\":{\"needle\":42},\"other\":true}", TestContext.Current.CancellationToken);
        var window = new MainWindow();
        window.Show();
        try
        {
            await window.OpenDocumentAsync(path);
            await window.ToggleOutlineAsync();
            var panel = window.FindControl<OutlinePanel>("OutlineSidebar")!;
            var editor = window.FindControl<TextEditor>("Editor")!;
            var query = panel.FindControl<TextBox>("Query")!;
            var original = panel.Nodes[0].Children[0];
            panel.Navigate(original);
            Assert.Equal("needle", editor.SelectedText);
            Assert.EndsWith("outer › needle", window.State.Breadcrumb);
            query.Text = "needle";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("outer", Assert.Single(panel.Nodes).Name);
            Assert.Equal("needle", Assert.Single(panel.Nodes[0].Children).Name);
            editor.Document.Insert(0, "\n\n");
            var offset = editor.CaretOffset;
            panel.Navigate(original);
            Assert.Equal(offset, editor.CaretOffset);
            await panel.RefreshAsync();
            panel.Navigate(original);
            Assert.Equal(offset, editor.CaretOffset);
            panel.Navigate(panel.Nodes[0].Children[0]);
            Assert.Equal("needle", editor.SelectedText);
            Assert.Equal(3, editor.TextArea.Caret.Line);
            query.Text = "";
            Dispatcher.UIThread.RunJobs();
            panel.Nodes[0].IsExpanded = false;
            await panel.RefreshAsync();
            Assert.False(panel.Nodes[0].IsExpanded);
            var pinned = window.State.ActiveDocument!;
            panel.TogglePin();
            Assert.True(panel.IsPinned);
            window.NewDocument();
            var empty = window.State.ActiveTab;
            await panel.RefreshAsync();
            Assert.NotEmpty(panel.Nodes);
            panel.Navigate(panel.Nodes[0].Children[0]);
            Assert.Same(pinned, window.State.ActiveDocument);
            Assert.Equal("needle", editor.SelectedText);
            window.State.ActiveTab = empty;
            window.State.Tabs.Remove(pinned);
            await panel.RefreshAsync();
            Assert.False(panel.IsPinned);
            Assert.Empty(panel.Nodes);
            Assert.Equal("", window.State.CurrentSymbol);
        }
        finally
        {
            foreach (var tab in window.State.Tabs.OfType<Zyber.Models.EditorTab>()) tab.Document.UndoStack.MarkAsOriginalFile();
            window.Close();
            Directory.Delete(root, true);
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Zyber.Languages;
using Xunit;

namespace Zyber.Tests;

public sealed class LanguageServerTests
{
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FramesUseUtf8ByteLengthsAndSurviveFragmentedReads()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Cancellation);
        using var server = await listener.AcceptTcpClientAsync(Cancellation);
        await using var connection = new JsonRpcConnection(client.GetStream(), client.GetStream());
        var request = connection.RequestAsync("echo", new { text = "😀 café" }, Cancellation);
        var received = await JsonRpcConnection.ReadMessageAsync(server.GetStream(), Cancellation);
        Assert.Equal("😀 café", received.GetProperty("params").GetProperty("text").GetString());
        var bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + received.GetProperty("id").GetInt32() + ",\"result\":\"😀 café\"}");
        var frame = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n").Concat(bytes).ToArray();
        foreach (var value in frame) await server.GetStream().WriteAsync(new[] { value }, Cancellation);
        Assert.Equal("😀 café", (await request.WaitAsync(TimeSpan.FromSeconds(5), Cancellation)).GetString());
    }

    [Theory]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: 999999999\r\n\r\n")]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}")]
    [InlineData("Other: 2\r\n\r\n{}")]
    public async Task InvalidFrameLengthsAreRejectedBeforeAllocating(string frame)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(frame));
        await Assert.ThrowsAsync<IOException>(() => JsonRpcConnection.ReadMessageAsync(stream, Cancellation));
    }

    [Fact]
    public async Task CancellingARequestNotifiesTheServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Cancellation);
        using var server = await listener.AcceptTcpClientAsync(Cancellation);
        await using var connection = new JsonRpcConnection(client.GetStream(), client.GetStream());
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var request = connection.RequestAsync("slow", null, cancel.Token);
        var original = await JsonRpcConnection.ReadMessageAsync(server.GetStream(), Cancellation);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var cancellation = await JsonRpcConnection.ReadMessageAsync(server.GetStream(), Cancellation);
        Assert.Equal("$/cancelRequest", cancellation.GetProperty("method").GetString());
        Assert.Equal(original.GetProperty("id").GetInt32(), cancellation.GetProperty("params").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ServerDisconnectFailsPendingRequestsWithoutHanging()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Cancellation);
        using var server = await listener.AcceptTcpClientAsync(Cancellation);
        await using var connection = new JsonRpcConnection(client.GetStream(), client.GetStream());
        var request = connection.RequestAsync("never-answered", null, Cancellation);
        await JsonRpcConnection.ReadMessageAsync(server.GetStream(), Cancellation);
        server.Close();
        await Assert.ThrowsAsync<IOException>(() => request.WaitAsync(TimeSpan.FromSeconds(5), Cancellation));
    }

    [Fact]
    public void PositionsAndEditsPreserveUtf16AndRejectOverlaps()
    {
        const string text = "😀 café\r\nnext\n";
        Assert.Equal(new LspPosition(0, 3), LspText.Position(text, 3));
        Assert.Equal(9, LspText.Offset(text, new(1, 0)));
        Assert.Equal(7, LspText.Offset(text, new(0, 99)));
        Assert.Equal(text.Length, LspText.Offset(text, new(99, 0)));
        Assert.Throws<IOException>(() => LspText.Offset(text, new(-1, 0)));
        Assert.Throws<IOException>(() => LspText.ValidateEdits(text, new[]
        {
            new LspTextEdit(new(new(0, 0), new(0, 4)), "one"),
            new LspTextEdit(new(new(0, 3), new(0, 5)), "two")
        }));
    }

    [Theory]
    [InlineData("one\rtwo\r\n😀", 9, 2, 0)]
    [InlineData("one\rtwo\r\n😀", 11, 2, 2)]
    [InlineData("one\rtwo\r\n😀", 6, 1, 2)]
    public void MixedLineEndingsKeepLanguageServerPositionsAligned(string text, int offset, int line, int character)
    {
        var position = new LspPosition(line, character);
        Assert.Equal(position, LspText.Position(text, offset));
        Assert.Equal(offset, LspText.Offset(text, position));
    }

    [Fact]
    public async Task RealPyrightReportsDiagnosticsCompletionHoverAndDefinition()
    {
        var script = Environment.GetEnvironmentVariable("ZYBER_TEST_PYRIGHT");
        if (string.IsNullOrEmpty(script)) Assert.Skip("Set ZYBER_TEST_PYRIGHT to the installed Pyright langserver.index.js to run the real-server integration test.");
        Assert.True(File.Exists(script), "The configured Pyright script does not exist.");
        var directory = Path.Combine(Path.GetTempPath(), "zyber-lsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.py");
        const string source = "def greet(name: str) -> str:\n    return 'hello ' + name\n\nnumber: int = 'wrong'\nresult = greet('Zyber')\nresult.\n";
        await File.WriteAllTextAsync(path, source, Cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var configuration = new LanguageServerConfiguration("pyright", LanguageServerRegistry.FindExecutable("node")!, new[] { script!, "--stdio" });
            await using var session = await LanguageServerSession.StartAsync(configuration, directory, timeout.Token);
            var reports = Channel.CreateUnbounded<PublishedDiagnostics>();
            session.Diagnostics += report => reports.Writer.TryWrite(report);
            await session.OpenAsync(path, "python", source, 1, timeout.Token);
            PublishedDiagnostics report;
            do { report = await reports.Reader.ReadAsync(timeout.Token); }
            while (report.Uri != LspText.FileUri(path) || report.Diagnostics.Count == 0);
            Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Range.Start.Line == 3 && diagnostic.Severity == 1);
            var completions = LanguageServerSession.Completions(await session.RequestAtAsync("textDocument/completion", path, new(5, 7), timeout.Token));
            Assert.Contains(completions, completion => completion.Label == "upper");
            var definition = LanguageServerSession.Locations(await session.RequestAtAsync("textDocument/definition", path, new(4, 11), timeout.Token));
            Assert.Equal(0, Assert.Single(definition).Range.Start.Line);
            var hover = await session.RequestAtAsync("textDocument/hover", path, new(4, 11), timeout.Token);
            Assert.Contains("greet", LspText.Documentation(hover.GetProperty("contents")));
            var outline = Zyber.Services.DocumentOutline.FromLanguageServer(source, path, await session.DocumentSymbolsAsync(path, timeout.Token));
            var function = Assert.Single(outline.Symbols, symbol => symbol.Name == "greet");
            Assert.Equal("greet", source[function.SelectionStart..function.SelectionEnd]);
            var changed = source.Replace("'wrong'", "42").Replace("result.\n", "result\n");
            await session.ChangeAsync(path, source, changed, 2, timeout.Token);
            do { report = await reports.Reader.ReadAsync(timeout.Token); }
            while (report.Uri != LspText.FileUri(path) || report.Version != 2);
            Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Severity == 1);
            await session.CloseAsync(path, timeout.Token);
        }
        finally { Directory.Delete(directory, true); }
    }
}

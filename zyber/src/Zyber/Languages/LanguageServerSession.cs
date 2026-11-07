using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Zyber.Languages;

public sealed record LanguageServerConfiguration(string Name, string Command, IReadOnlyList<string> Arguments, JsonElement Settings = default);

public sealed class LanguageServerSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly JsonRpcConnection connection;
    private readonly Task errors;
    private readonly StringBuilder errorTail = new();
    private readonly string root;
    private readonly JsonElement settings;
    private bool disposed;
    private int changeKind;
    private bool openClose;
    private bool save;
    private bool saveText;
    public JsonElement Capabilities { get; private set; }
    public string Name { get; }
    public event Action<PublishedDiagnostics>? Diagnostics;
    public event Action<string>? Status;

    private LanguageServerSession(Process process, LanguageServerConfiguration configuration, string root)
    {
        this.process = process;
        this.root = root;
        settings = configuration.Settings;
        Name = configuration.Name;
        connection = new(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        connection.HandleRequest = HandleServerRequestAsync;
        connection.Notification += OnNotification;
        connection.Failed += exception => { if (!disposed) Status?.Invoke(Name + " stopped: " + exception.Message); };
        errors = ReadErrorsAsync();
    }

    public static async Task<LanguageServerSession> StartAsync(LanguageServerConfiguration configuration, string root, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(configuration.Command)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in configuration.Arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new IOException("Could not start " + configuration.Name);
        var session = new LanguageServerSession(process, configuration, root);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await session.connection.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                clientInfo = new { name = "Zyber", version = "0.1.0" },
                rootUri = LspText.FileUri(root),
                workspaceFolders = new[] { new { uri = LspText.FileUri(root), name = Path.GetFileName(root) } },
                capabilities = new
                {
                    general = new { positionEncodings = new[] { "utf-16" } },
                    workspace = new { configuration = true, workspaceFolders = true, applyEdit = false },
                    textDocument = new
                    {
                        synchronization = new { dynamicRegistration = false, didSave = true },
                        publishDiagnostics = new { relatedInformation = false, versionSupport = true },
                        completion = new { completionItem = new { snippetSupport = false, insertReplaceSupport = true, documentationFormat = new[] { "plaintext", "markdown" } } },
                        hover = new { contentFormat = new[] { "plaintext", "markdown" } },
                        definition = new { linkSupport = true },
                        references = new { dynamicRegistration = false },
                        formatting = new { dynamicRegistration = false },
                        documentSymbol = new { hierarchicalDocumentSymbolSupport = true }
                    }
                }
            }, timeout.Token).ConfigureAwait(false);
            session.Capabilities = result.GetProperty("capabilities").Clone();
            if (session.Capabilities.TryGetProperty("positionEncoding", out var encoding) && encoding.GetString() != "utf-16")
                throw new IOException(configuration.Name + " requires an unsupported position encoding: " + encoding.GetString());
            if (session.Capabilities.TryGetProperty("textDocumentSync", out var sync))
            {
                if (sync.ValueKind == JsonValueKind.Number) { session.changeKind = sync.GetInt32(); session.openClose = session.changeKind != 0; }
                else if (sync.ValueKind == JsonValueKind.Object)
                {
                    session.changeKind = sync.TryGetProperty("change", out var change) ? change.GetInt32() : 0;
                    session.openClose = sync.TryGetProperty("openClose", out var openClose) && openClose.ValueKind == JsonValueKind.True;
                    if (sync.TryGetProperty("save", out var save))
                    {
                        session.save = save.ValueKind is JsonValueKind.True or JsonValueKind.Object;
                        session.saveText = save.ValueKind == JsonValueKind.Object && save.TryGetProperty("includeText", out var includeText) && includeText.ValueKind == JsonValueKind.True;
                    }
                }
            }
            await session.connection.NotifyAsync("initialized", new { }, timeout.Token).ConfigureAwait(false);
            if (configuration.Settings.ValueKind == JsonValueKind.Object)
                await session.connection.NotifyAsync("workspace/didChangeConfiguration", new { settings = configuration.Settings }, timeout.Token).ConfigureAwait(false);
            return session;
        }
        catch (Exception exception)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            string details;
            lock (session.errorTail) details = session.errorTail.ToString().Trim();
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new IOException("Could not initialize " + configuration.Name + ": " + exception.Message + (details.Length > 0 ? "\n" + details : ""), exception);
        }
    }

    public bool Supports(string capability) => Capabilities.TryGetProperty(capability, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False);
    public Task OpenAsync(string path, string languageId, string text, int version, CancellationToken cancellationToken = default) => openClose
        ? connection.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri = LspText.FileUri(path), languageId, version, text } }, cancellationToken) : Task.CompletedTask;
    public Task ChangeAsync(string path, string oldText, string text, int version, CancellationToken cancellationToken = default)
    {
        if (changeKind == 0) return Task.CompletedTask;
        object change = changeKind == 2 ? new { range = new LspRange(new(0, 0), LspText.Position(oldText, oldText.Length)), text } : new { text };
        return connection.NotifyAsync("textDocument/didChange", new { textDocument = new { uri = LspText.FileUri(path), version }, contentChanges = new[] { change } }, cancellationToken);
    }
    public Task CloseAsync(string path, CancellationToken cancellationToken = default) => openClose
        ? connection.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = LspText.FileUri(path) } }, cancellationToken) : Task.CompletedTask;
    public Task SaveAsync(string path, string text, CancellationToken cancellationToken = default) => save
        ? connection.NotifyAsync("textDocument/didSave", saveText ? (object)new { textDocument = new { uri = LspText.FileUri(path) }, text } : new { textDocument = new { uri = LspText.FileUri(path) } }, cancellationToken) : Task.CompletedTask;

    public Task<JsonElement> RequestAtAsync(string method, string path, LspPosition position, CancellationToken cancellationToken = default) =>
        connection.RequestAsync(method, new { textDocument = new { uri = LspText.FileUri(path) }, position }, cancellationToken);
    public Task<JsonElement> FormatAsync(string path, int tabSize, bool insertSpaces, CancellationToken cancellationToken = default) =>
        connection.RequestAsync("textDocument/formatting", new { textDocument = new { uri = LspText.FileUri(path) }, options = new { tabSize, insertSpaces } }, cancellationToken);
    public Task<JsonElement> DocumentSymbolsAsync(string path, CancellationToken cancellationToken = default) =>
        connection.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri = LspText.FileUri(path) } }, cancellationToken);
    public Task<JsonElement> ReferencesAsync(string path, LspPosition position, CancellationToken cancellationToken = default) =>
        connection.RequestAsync("textDocument/references", new { textDocument = new { uri = LspText.FileUri(path) }, position, context = new { includeDeclaration = true } }, cancellationToken);

    public static IReadOnlyList<LspCompletion> Completions(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null) return [];
        var items = result.ValueKind == JsonValueKind.Array ? result : result.GetProperty("items");
        var completions = new List<LspCompletion>();
        foreach (var item in items.EnumerateArray())
        {
            // Snippets were explicitly declined in initialize; do not insert unsupported snippet syntax.
            if (item.TryGetProperty("insertTextFormat", out var format) && format.GetInt32() == 2) continue;
            var label = item.GetProperty("label").GetString()!;
            LspTextEdit? edit = null;
            if (item.TryGetProperty("textEdit", out var textEdit))
                edit = textEdit.TryGetProperty("range", out _) ? LspText.Edit(textEdit)
                    : new(LspText.Range(textEdit.GetProperty("replace")), textEdit.GetProperty("newText").GetString()!);
            var insertText = item.TryGetProperty("insertText", out var insert) ? insert.GetString()! : label;
            var additional = item.TryGetProperty("additionalTextEdits", out var changes) ? changes.EnumerateArray().Select(LspText.Edit).ToArray() : [];
            completions.Add(new(label, item.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
                item.TryGetProperty("documentation", out var documentation) ? LspText.Documentation(documentation) : null,
                insertText, edit, additional, item.TryGetProperty("sortText", out var sort) ? sort.GetString()! : label));
        }
        return completions.OrderBy(item => item.SortText, StringComparer.Ordinal).ToArray();
    }
    public static IReadOnlyList<LspLocation> Locations(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null) return [];
        var items = result.ValueKind == JsonValueKind.Array ? result.EnumerateArray().ToArray() : new[] { result };
        return items.Select(item => item.TryGetProperty("targetUri", out var target)
            ? new LspLocation(target.GetString()!, LspText.Range(item.GetProperty("targetSelectionRange")))
            : new LspLocation(item.GetProperty("uri").GetString()!, LspText.Range(item.GetProperty("range"))))
            .Where(item => Uri.TryCreate(item.Uri, UriKind.Absolute, out var uri) && uri.IsFile).ToArray();
    }

    private Task<object?> HandleServerRequestAsync(string method, JsonElement parameters)
    {
        object? result = method switch
        {
            "workspace/configuration" => parameters.GetProperty("items").EnumerateArray().Select(ConfigurationFor).ToArray(),
            "workspace/workspaceFolders" => new[] { new { uri = LspText.FileUri(root), name = Path.GetFileName(root) } },
            "window/workDoneProgress/create" => null,
            "window/showMessageRequest" => null,
            "workspace/applyEdit" => new { applied = false, failureReason = "Unsolicited workspace edits are not supported yet." },
            _ => throw new LanguageServerException("Method not supported: " + method, -32601)
        };
        return Task.FromResult(result);
    }
    private object? ConfigurationFor(JsonElement item)
    {
        if (settings.ValueKind != JsonValueKind.Object) return null;
        var selected = settings;
        if (item.TryGetProperty("section", out var section) && section.GetString() is { Length: > 0 } name)
            foreach (var part in name.Split('.'))
            {
                if (selected.ValueKind != JsonValueKind.Object || !selected.TryGetProperty(part, out var next)) return null;
                selected = next;
            }
        return selected;
    }
    private void OnNotification(string method, JsonElement parameters)
    {
        try
        {
            if (method == "textDocument/publishDiagnostics")
            {
                var diagnostics = parameters.GetProperty("diagnostics").EnumerateArray().Select(item => new LspDiagnostic(
                    LspText.Range(item.GetProperty("range")), item.TryGetProperty("severity", out var severity) ? severity.GetInt32() : 1,
                    item.GetProperty("message").GetString()!, item.TryGetProperty("source", out var source) ? source.GetString() : null)).ToArray();
                Diagnostics?.Invoke(new(parameters.GetProperty("uri").GetString()!, parameters.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number ? version.GetInt32() : null, diagnostics));
            }
            else if (method == "window/showMessage") Status?.Invoke(Name + ": " + parameters.GetProperty("message").GetString());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            Status?.Invoke(Name + " sent an invalid notification: " + exception.Message);
        }
    }
    private async Task ReadErrorsAsync()
    {
        try
        {
            var buffer = new char[1024];
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                lock (errorTail)
                {
                    errorTail.Append(buffer, 0, count);
                    if (errorTail.Length > 8192) errorTail.Remove(0, errorTail.Length - 8192);
                }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (!process.HasExited)
            {
                await connection.RequestAsync("shutdown", null, timeout.Token).ConfigureAwait(false);
                await connection.NotifyAsync("exit", null, timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidOperationException) { }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await connection.DisposeAsync().ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            process.Dispose();
        }
    }
}

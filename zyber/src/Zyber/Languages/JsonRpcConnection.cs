using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zyber.Languages;

public sealed class LanguageServerException(string message, int code) : IOException(message)
{
    public int Code { get; } = code;
}

public sealed class JsonRpcConnection : IAsyncDisposable
{
    private const int MaximumMessageBytes = 32 * 1024 * 1024;
    private readonly Stream input;
    private readonly Stream output;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Task reader;
    private int nextId;
    private volatile Exception? failure;
    public event Action<string, JsonElement>? Notification;
    public event Action<Exception>? Failed;
    public Func<string, JsonElement, Task<object?>>? HandleRequest { get; set; }

    public JsonRpcConnection(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
        reader = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        if (failure is not null) throw new IOException("Language server connection is closed.", failure);
        lifetime.Token.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref nextId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = response;
        if (failure is not null) response.TrySetException(new IOException("Language server connection is closed.", failure));
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await NotifyAsync("$/cancelRequest", new { id }, lifetime.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
            throw;
        }
        finally { pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken = default) =>
        SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaximumMessageBytes) throw new IOException("Language server message exceeds the 32 MiB limit.");
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await writes.WaitAsync(combined.Token).ConfigureAwait(false);
        try
        {
            // Once a frame starts, finish it even if the caller cancels; a partial frame corrupts stdio.
            await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), lifetime.Token).ConfigureAwait(false);
            await output.WriteAsync(bytes, lifetime.Token).ConfigureAwait(false);
            await output.FlushAsync(lifetime.Token).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(input, lifetime.Token).ConfigureAwait(false);
                if (message.TryGetProperty("method", out var methodValue))
                {
                    var method = methodValue.GetString() ?? throw new IOException("Missing language server method.");
                    var parameters = message.TryGetProperty("params", out var value) ? value : default;
                    if (message.TryGetProperty("id", out var id))
                    {
                        object response;
                        try
                        {
                            if (HandleRequest is null) throw new LanguageServerException("Method not supported", -32601);
                            var result = await HandleRequest(method, parameters).ConfigureAwait(false);
                            response = new { jsonrpc = "2.0", id, result };
                        }
                        catch (LanguageServerException exception)
                        {
                            response = new { jsonrpc = "2.0", id, error = new { code = exception.Code, message = exception.Message } };
                        }
                        await SendAsync(response, lifetime.Token).ConfigureAwait(false);
                    }
                    else Notification?.Invoke(method, parameters);
                }
                else if (message.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && pending.TryRemove(number, out var completion))
                {
                    if (message.TryGetProperty("error", out var error))
                        completion.TrySetException(new LanguageServerException(error.GetProperty("message").GetString() ?? "Language server error", error.GetProperty("code").GetInt32()));
                    else completion.TrySetResult(message.GetProperty("result").Clone());
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException or ObjectDisposedException)
        {
            failure = exception;
            Failed?.Invoke(exception);
        }
        finally
        {
            failure ??= new IOException("Language server connection closed.");
            foreach (var (id, completion) in pending)
                if (pending.TryRemove(id, out _)) completion.TrySetException(new IOException("Language server connection closed.", failure));
        }
    }

    public static async Task<JsonElement> ReadMessageAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) == 0) throw new EndOfStreamException("Language server closed stdout.");
            header.Append((char)buffer[0]);
            if (header.Length > 16 * 1024) throw new IOException("Language server header is too large.");
            if (header.Length >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
        }
        int? length = null;
        foreach (var line in header.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator < 0) throw new IOException("Malformed language server header.");
            if (!line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (length is not null || !int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0 || parsed > MaximumMessageBytes)
                throw new IOException("Invalid language server Content-Length.");
            length = parsed;
        }
        if (length is null) throw new IOException("Language server omitted Content-Length.");
        var body = new byte[length.Value];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Language server message must be an object.");
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await reader.ConfigureAwait(false);
        // The process owner owns the streams. Do not dispose the write lock while a cancelled sender unwinds.
    }
}

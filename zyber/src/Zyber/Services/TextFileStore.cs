using System.Security.Cryptography;
using System.Text;

namespace Zyber.Services;

public sealed record TextFileSnapshot(string Path, string Text, Encoding Encoding, string Fingerprint);

public sealed class FileConflictException(string path)
    : IOException($"'{System.IO.Path.GetFileName(path)}' changed on disk. Save a copy to keep both versions.");

public static class TextFileStore
{
    public const long MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<TextFileSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = System.IO.Path.GetFullPath(path);
        var information = new FileInfo(path);
        if (information.LinkTarget is not null)
            path = information.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Cannot resolve this file's symbolic link.");
        var bytes = await ReadBytesAsync(path, cancellationToken);
        Encoding encoding = StrictUtf8;
        var offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            encoding = new UTF8Encoding(true, true);
            offset = 3;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }) ||
                 bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            throw new IOException("UTF-32 files are not supported in this version.");
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            encoding = new UnicodeEncoding(false, true, true);
            offset = 2;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            encoding = new UnicodeEncoding(true, true, true);
            offset = 2;
        }
        string text;
        try { text = encoding.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException exception)
        {
            throw new IOException("This file is not supported UTF-8 or UTF-16 text. It was not modified.", exception);
        }
        if (text.Contains('\0')) throw new IOException("This appears to be a binary file. Binary editing is not available yet.");
        return new(path, text, encoding, Hash(bytes));
    }

    public static async Task<string> WriteAsync(string path, string text, Encoding encoding, string? expectedFingerprint)
    {
        path = System.IO.Path.GetFullPath(path);
        await VerifyUnchangedAsync(path, expectedFingerprint);
        var payload = encoding.GetBytes(text);
        if (payload.LongLength + encoding.Preamble.Length > MaximumFileBytes)
            throw new IOException("This version supports text files up to 16 MiB.");
        var bytes = new byte[encoding.Preamble.Length + payload.Length];
        encoding.Preamble.CopyTo(bytes);
        payload.CopyTo(bytes, encoding.Preamble.Length);
        var temporaryPath = path + ".zyber-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && File.Exists(path))
                File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(path));
            await VerifyUnchangedAsync(path, expectedFingerprint);
            if (expectedFingerprint is not null)
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path, overwrite: false);
            return Hash(bytes);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static async Task<string?> FingerprintAsync(string path) =>
        File.Exists(path) ? Hash(await ReadBytesAsync(path)) : null;

    private static async Task VerifyUnchangedAsync(string path, string? expected)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("The destination became a symbolic link. Reopen it before saving.");
        if (await FingerprintAsync(path) != expected) throw new FileConflictException(path);
    }

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new IOException("This version supports text files up to 16 MiB.");
        using var memory = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + count > MaximumFileBytes) throw new IOException("This version supports text files up to 16 MiB.");
            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return memory.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

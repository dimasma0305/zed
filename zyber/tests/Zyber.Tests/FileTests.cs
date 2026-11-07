using System.Text;
using Zyber.Services;
using Xunit;

namespace Zyber.Tests;

public sealed class FileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "zyber-tests-" + Guid.NewGuid().ToString("N"));
    public FileTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string FilePath => Path.Combine(directory, "sample.txt");

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public async Task SavePreservesEncodingAndLineEndings(string kind)
    {
        Encoding encoding = kind switch
        {
            "utf8-bom" => new UTF8Encoding(true, true),
            "utf16-le" => new UnicodeEncoding(false, true, true),
            "utf16-be" => new UnicodeEncoding(true, true, true),
            _ => new UTF8Encoding(false, true)
        };
        const string original = "Hello 🌍\r\nこんにちは\r\n";
        await File.WriteAllTextAsync(FilePath, original, encoding, TestContext.Current.CancellationToken);
        var snapshot = await TextFileStore.ReadAsync(FilePath, TestContext.Current.CancellationToken);
        Assert.Equal(original, snapshot.Text);
        var updated = original + "Zyber\r\n";
        await TextFileStore.WriteAsync(FilePath, updated, snapshot.Encoding, snapshot.Fingerprint);
        var expected = encoding.GetPreamble().Concat(encoding.GetBytes(updated)).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task SaveRefusesToOverwriteExternalChanges()
    {
        await File.WriteAllTextAsync(FilePath, "original", TestContext.Current.CancellationToken);
        var snapshot = await TextFileStore.ReadAsync(FilePath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(FilePath, "changed elsewhere", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<FileConflictException>(() => TextFileStore.WriteAsync(FilePath, "mine", snapshot.Encoding, snapshot.Fingerprint));
        Assert.Equal("changed elsewhere", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewFileSaveRefusesAnUnexpectedExistingFile()
    {
        await File.WriteAllTextAsync(FilePath, "other work", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<FileConflictException>(() => TextFileStore.WriteAsync(FilePath, "mine", Encoding.UTF8, null));
        Assert.Equal("other work", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveDoesNotRecreateAnExternallyDeletedFile()
    {
        await File.WriteAllTextAsync(FilePath, "original", TestContext.Current.CancellationToken);
        var snapshot = await TextFileStore.ReadAsync(FilePath, TestContext.Current.CancellationToken);
        File.Delete(FilePath);
        await Assert.ThrowsAsync<FileConflictException>(() => TextFileStore.WriteAsync(FilePath, "mine", snapshot.Encoding, snapshot.Fingerprint));
        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 2, 3 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0, 0, 65, 0, 0, 0 })]
    public async Task UnsupportedFilesAreRejectedWithoutModification(byte[] bytes)
    {
        await File.WriteAllBytesAsync(FilePath, bytes, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => TextFileStore.ReadAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedFileIsRejected()
    {
        using (var stream = File.Create(FilePath)) stream.SetLength(TextFileStore.MaximumFileBytes + 1);
        await Assert.ThrowsAsync<IOException>(() => TextFileStore.ReadAsync(FilePath, TestContext.Current.CancellationToken));
    }
}

using IIDXProgressDashboard.Master;
using System.Text;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class TextageSourceReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "TextageSourceReaderTests", Guid.NewGuid().ToString("N"));

    public TextageSourceReaderTests()
    {
        Directory.CreateDirectory(directory);
        // 実データを使わず、JSが実行されず文字列のまま渡ることを確認する。
        foreach (var name in TextageSourceReader.RequiredFileNames)
            File.WriteAllText(Path.Combine(directory, name), "throw new Error('合成入力');", new UTF8Encoding(true));
    }

    [Fact]
    public async Task ReadsWithoutExecutingAndPreservesInput()
    {
        var path = Path.Combine(directory, "titletbl.js");
        var original = File.ReadAllBytes(path);
        var snapshot = await new TextageSourceReader().ReadAsync(directory);
        Assert.Equal(7, snapshot.Files.Count);
        var file = snapshot.Files["titletbl.js"];
        Assert.Equal("throw new Error('合成入力');", file.Content);
        Assert.Equal(original.LongLength, file.ByteLength);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), file.Sha256);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ReportsAllMissingFiles()
    {
        File.Delete(Path.Combine(directory, "actbl.js"));
        File.Delete(Path.Combine(directory, "datatbl.js"));
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => new TextageSourceReader().ReadAsync(directory));
        Assert.Contains("actbl.js", error.Message);
        Assert.Contains("datatbl.js", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsInvalidUtf8AndEmptyInput(bool invalidEncoding)
    {
        File.WriteAllBytes(Path.Combine(directory, "titletbl.js"), invalidEncoding ? new byte[] { 0xff } : Encoding.UTF8.GetBytes(" \r\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new TextageSourceReader().ReadAsync(directory));
    }

    [Fact]
    public async Task SupportsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TextageSourceReader().ReadAsync(directory, cancellation.Token));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

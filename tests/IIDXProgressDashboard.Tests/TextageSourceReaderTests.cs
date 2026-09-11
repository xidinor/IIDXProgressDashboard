using IIDXProgressDashboard.Master;
using System.Text;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class TextageSourceReaderTests : IDisposable
{
    // Reader側の定数からfixtureを生成せず、公開された入力契約を独立に固定する。
    private static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>
    {
        ["titletbl.js"] = "titletbl={'sample':[1,1,0,\"ジャンル\",\"作者\",\"合成曲\"]};",
        ["scrlist.js"] = "vertbl=[\"Consumer only\",\"1st style\"];",
        ["datatbl.js"] = "datatbl={'sample':[0,0,100,0,0,0,0,0,0,0,0,\"120\"]};",
        ["actbl.js"] = "actbl={'sample':[1,0,0,0,0,1,7,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]};",
        ["cstbl.js"] = "cstbl=[]; cstbl[1]={};",
        ["cstbl1.js"] = "cstbl[3]={};",
        ["cstbl2.js"] = "cstbl[9]={};"
    };

    private readonly string directory = Path.Combine(Path.GetTempPath(), "TextageSourceReaderTests", Guid.NewGuid().ToString("N"));

    public TextageSourceReaderTests()
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, content) in Sources)
            File.WriteAllText(Path.Combine(directory, name), content, new UTF8Encoding(false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsUtf8AndPreservesOriginalBytes(bool withBom)
    {
        // BOMと改行・結合文字を含め、文字列正規化や再エンコードで同一性を変えない。
        foreach (var (name, content) in Sources)
            File.WriteAllText(Path.Combine(directory, name), content + "\r\n// e\u0301　\n", new UTF8Encoding(withBom));
        var originals = Sources.Keys.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(directory, name)));
        var snapshot = await new TextageSourceReader().ReadAsync(directory);
        Assert.Equal(7, snapshot.Files.Count);
        foreach (var (name, original) in originals)
        {
            var file = snapshot.Files[name];
            Assert.Equal(name, file.Name);
            Assert.Equal(Sources[name] + "\r\n// e\u0301　\n", file.Content);
            Assert.Equal(original.LongLength, file.ByteLength);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), file.Sha256);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, name)));
        }
    }

    [Theory]
    [InlineData("これはTextageではありません。???")]
    [InlineData("throw new Error('実行してはならない');")]
    [InlineData("while (true) {}")]
    public async Task ReturnsUtf8TextWithoutParsingOrExecuting(string content)
    {
        var path = Path.Combine(directory, "titletbl.js");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        var original = File.ReadAllBytes(path);
        var snapshot = await new TextageSourceReader().ReadAsync(directory);
        Assert.Equal(content, snapshot.Files["titletbl.js"].Content);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsWrongFileNameRegardlessOfContent(bool validContent)
    {
        File.Move(Path.Combine(directory, "titletbl.js"), Path.Combine(directory, "other.js"));
        if (!validContent) File.WriteAllBytes(Path.Combine(directory, "other.js"), new byte[] { 0xff });
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => new TextageSourceReader().ReadAsync(directory));
        Assert.Contains("titletbl.js", error.Message);
    }

    [Fact]
    public async Task ReturnsSwappedContentsWithoutSemanticValidation()
    {
        File.WriteAllText(Path.Combine(directory, "titletbl.js"), Sources["datatbl.js"]);
        File.WriteAllText(Path.Combine(directory, "datatbl.js"), Sources["titletbl.js"]);
        var snapshot = await new TextageSourceReader().ReadAsync(directory);
        Assert.Equal(Sources["datatbl.js"], snapshot.Files["titletbl.js"].Content);
        Assert.Equal(Sources["titletbl.js"], snapshot.Files["datatbl.js"].Content);
    }

    [Fact]
    public async Task IgnoresUnrelatedFileEvenWhenItsEncodingIsInvalid()
    {
        var path = Path.Combine(directory, "unrelated.js");
        var bytes = new byte[] { 0xff, 0xfe, 0x80 };
        File.WriteAllBytes(path, bytes);
        var snapshot = await new TextageSourceReader().ReadAsync(directory);
        Assert.Equal(Sources.Keys.OrderBy(name => name), snapshot.Files.Keys.OrderBy(name => name));
        foreach (var (name, content) in Sources) Assert.Equal(content, snapshot.Files[name].Content);
        Assert.Equal(bytes, File.ReadAllBytes(path));
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
    [InlineData("")]
    [InlineData(" \r\n\t　")]
    [InlineData("\uFEFF")]
    public async Task RejectsEmptyOrWhitespaceInput(string content)
    {
        await AssertRejectedBytesAsync(Encoding.UTF8.GetBytes(content));
    }

    public static TheoryData<byte[]> InvalidUtf8Inputs => new()
    {
        new byte[] { 0xff, 0x00, 0x80, 0xfe }, // 再現可能なバイナリ。乱数には依存しない。
        new byte[] { 0xe3, 0x81 },             // 途中で切れたUTF-8。
        new byte[] { 0xc0, 0xaf },             // 不正な冗長表現。
        new byte[] { 0x82, 0xa0 },             // CP932の「あ」。別文字コードへfallbackしない。
        new byte[] { 0xff, 0xfe, 0x41, 0x00 }  // UTF-16 BOMでも自動判定しない。
    };

    [Theory]
    [MemberData(nameof(InvalidUtf8Inputs))]
    public async Task RejectsInvalidUtf8WithoutFallbackOrReplacement(byte[] bytes)
    {
        await AssertRejectedBytesAsync(bytes);
    }

    private async Task AssertRejectedBytesAsync(byte[] bytes)
    {
        // 最後の必須ファイルで失敗してもsnapshotを返さず、入力を補正しない。
        var path = Path.Combine(directory, "cstbl2.js");
        File.WriteAllBytes(path, bytes);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new TextageSourceReader().ReadAsync(directory));
        Assert.Contains("cstbl2.js", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(path));
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

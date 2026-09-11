using System.Net;
using System.Net.Http;
using System.Text;
using IIDXProgressDashboard.Master;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class MasterDataProviderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MasterDataProviderTests", Guid.NewGuid().ToString("N"));
    private static Encoding Cp932
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
    }

    [Theory]
    [InlineData(TextageEncoding.Utf8)]
    [InlineData(TextageEncoding.Cp932)]
    public async Task ReadsFourLocalFilesUsingExplicitEncoding(TextageEncoding encoding)
    {
        Directory.CreateDirectory(directory);
        var originals = new Dictionary<string, byte[]>();
        foreach (var (name, content) in TextageMasterParserTests.Fixture())
        {
            var bytes = (encoding == TextageEncoding.Utf8 ? Encoding.UTF8 : Cp932).GetBytes(content);
            originals[name] = bytes;
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
        }
        var result = await new MasterDataProvider().ReadLocalAsync(directory, new(encoding));
        Assert.Equal(10, result.Charts.Count);
        Assert.Equal("曲名 副題", result.Songs[0].Title);
        Assert.Equal(4, result.Sources.Files.Count);
        foreach (var (name, bytes) in originals)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, name)));
            var file = result.Sources.Files[name];
            Assert.Equal(encoding, file.Encoding);
            Assert.Equal(bytes.LongLength, file.ByteLength);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), file.Sha256);
            Assert.NotNull(file.StartedAt);
            Assert.True(file.CompletedAt >= file.StartedAt);
        }
        Assert.Equal(4, Directory.GetFiles(directory).Length); // DB等を生成しない。
    }

    [Fact]
    public async Task FetchesAndRevalidatesBeforeReturningModels()
    {
        using var handler = new FakeHandler((name, _) => Response(TextageMasterParserTests.Fixture()[name]));
        using var client = new HttpClient(handler);
        var result = await new MasterDataProvider().FetchAsync(client);
        Assert.Equal(8, handler.Calls);
        Assert.Equal(10, result.Charts.Count);
        Assert.All(result.Sources.Files.Values, file =>
        {
            Assert.Equal(TextageEncoding.Cp932, file.Encoding);
            Assert.StartsWith("https://textage.cc/score/", file.Location);
        });
        Assert.False(result.CanDeactivateMissing);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("decode")]
    [InlineData("empty")]
    [InlineData("syntax")]
    [InlineData("changed")]
    [InlineData("recheckHttp")]
    public async Task PropagatesFailureInsteadOfReturningPartialMaster(string mode)
    {
        using var handler = new FakeHandler((name, call) =>
        {
            if (mode == "http" || mode == "recheckHttp" && call > 4) return new(HttpStatusCode.ServiceUnavailable);
            if (mode == "decode") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x82 }) };
            if (mode == "empty") return Response("");
            if (mode == "syntax") return Response("execute();");
            var text = TextageMasterParserTests.Fixture()[name];
            if (mode == "changed" && call > 4) text += " // changed";
            return Response(text);
        });
        using var client = new HttpClient(handler);
        if (mode is "http" or "recheckHttp") await Assert.ThrowsAsync<HttpRequestException>(() => new MasterDataProvider().FetchAsync(client));
        else await Assert.ThrowsAsync<InvalidDataException>(() => new MasterDataProvider().FetchAsync(client));
    }

    [Fact]
    public async Task CancelsDuringAcquisitionWithoutStartingRemainingRequests()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FakeHandler((name, _) => { cancellation.Cancel(); return Response(TextageMasterParserTests.Fixture()[name]); });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MasterDataProvider().FetchAsync(client, cancellationToken: cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Cp932.GetBytes(text)) };
    private sealed class FakeHandler(Func<string, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(Path.GetFileName(request.RequestUri!.AbsolutePath), ++Calls));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

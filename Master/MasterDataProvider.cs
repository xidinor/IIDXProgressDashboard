using System.Collections.ObjectModel;
using System.Net.Http;

namespace IIDXProgressDashboard.Master;

/// <summary>取得と解析の入口。UI・DBを持たず、失敗時は候補を返さない。</summary>
public sealed class MasterDataProvider
{
    public static Uri SourceBaseUri { get; } = new("https://textage.cc/score/");

    public async Task<MasterSnapshot> ReadLocalAsync(string directory, TextageReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sources = await new TextageSourceReader().ReadAsync(directory, options ?? new(), cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => new TextageMasterParser().Parse(sources, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // HttpClientの寿命は呼出し側が管理する。合成HttpMessageHandlerで通信境界をテストできる。
    public async Task<MasterSnapshot> FetchAsync(HttpClient client, bool includeCsComparison = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var names = includeCsComparison ? TextageSourceReader.RequiredFileNames : TextageSourceReader.CatalogFileNames;
        var files = new Dictionary<string, TextageSourceFile>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var started = DateTimeOffset.UtcNow;
            using var response = await client.GetAsync(new Uri(SourceBaseUri, name), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            files.Add(name, TextageSourceReader.Decode(name, bytes, TextageEncoding.Cp932) with
            {
                Location = new Uri(SourceBaseUri, name).AbsoluteUri, StartedAt = started, CompletedAt = DateTimeOffset.UtcNow,
                ETag = response.Headers.ETag?.ToString(), LastModified = response.Content.Headers.LastModified
            });
        }
        // validatorがない配信にも対応し、同じ一式を再取得して元バイトで照合する。
        foreach (var name in names)
        {
            using var response = await client.GetAsync(new Uri(SourceBaseUri, name), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (TextageSourceReader.Hash(bytes) != files[name].Sha256) throw new InvalidDataException($"{name}: 取得中に入力が変更されました。");
        }
        var sources = new TextageSourceSnapshot(new ReadOnlyDictionary<string, TextageSourceFile>(files));
        return await Task.Run(() => new TextageMasterParser().Parse(sources, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Matching;

namespace IIDXProgressDashboard.Master;

public sealed record ExternalSong(long ExternalSongId, string Title, string NormalizedTitle, string Tag);
public sealed record ExternalSongSource(string Name, string Url, string Content, string Sha256);

/// <summary>元入力と解析結果を一緒に保持する。生成はProviderに限定する。</summary>
public sealed class ExternalSongSnapshot
{
    public IReadOnlyList<ExternalSong> Songs { get; }
    public IReadOnlyList<ExternalSongSource> Sources { get; }
    public DateTimeOffset AcquiredAt { get; }
    public string Fingerprint { get; }
    internal ExternalSongSnapshot(IEnumerable<ExternalSong> songs, IEnumerable<ExternalSongSource> sources, DateTimeOffset acquiredAt)
    {
        Songs = Array.AsReadOnly(songs.ToArray());
        Sources = Array.AsReadOnly(sources.ToArray());
        AcquiredAt = acquiredAt;
        Fingerprint = IidxDataTableProvider.Hash(Encoding.UTF8.GetBytes(string.Join("\n", Sources.Select(s => s.Name + ":" + s.Sha256))));
    }
}

/// <summary>独自ID・原曲名・tag対応を取得する。外部の曲名逆引きによる候補の間引きは利用しない。</summary>
public sealed class IidxDataTableProvider
{
    public const string SourceName = "IIDX_DATA_TABLE";
    public const int ParserVersion = 1;
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static Uri BaseUri { get; } = new("https://chinimuruhi.github.io/IIDX-Data-Table/textage/");
    private static readonly string[] Names = ["song-info.json", "title.json", "textage-tag.json"];
    private readonly SemaphoreSlim gate = new(1, 1);
    private ExternalSongSnapshot? cached;

    public async Task<ExternalSongSnapshot> FetchAsync(HttpClient client, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Providerインスタンスを再利用することで、連続操作による再取得を抑制する。
            if (cached is not null && DateTimeOffset.UtcNow - cached.AcquiredAt < TimeSpan.FromMinutes(5)) return cached;
            var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var name in Names) content.Add(name, await ReadAsync(client, name, token).ConfigureAwait(false));
            // 配信元に一式のrevision保証がないため、全3ファイルを再取得し同じバイト列か検証する。
            foreach (var name in Names)
            {
                var repeated = await ReadAsync(client, name, token).ConfigureAwait(false);
                if (!content[name].AsSpan().SequenceEqual(repeated))
                    throw new InvalidDataException("取得中に配信データが変化しました。反映しません。");
            }
            cached = Parse(content[Names[0]], content[Names[1]], content[Names[2]], DateTimeOffset.UtcNow);
            return cached;
        }
        finally { gate.Release(); }
    }

    public ExternalSongSnapshot Parse(byte[] songInfo, byte[] titles, byte[] tags, DateTimeOffset acquiredAt)
    {
        var bytes = new[] { songInfo, titles, tags };
        var sources = new List<ExternalSongSource>();
        var dictionaries = new List<Dictionary<long, JsonElement>>();
        for (var i = 0; i < Names.Length; i++)
        {
            if (bytes[i].Length is 0 or > MaximumBytes) throw new InvalidDataException("入力サイズが不正です。");
            var text = new UTF8Encoding(false, true).GetString(bytes[i]);
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("ID辞書が必要です。");
            var entries = new Dictionary<long, JsonElement>();
            foreach (var property in json.RootElement.EnumerateObject())
            {
                // JSONのキーは文字列だがIDは整数。先頭ゼロ・符号・重複キーを拒否する。
                if (!long.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 0 ||
                    id.ToString(CultureInfo.InvariantCulture) != property.Name || !entries.TryAdd(id, property.Value.Clone()))
                    throw new InvalidDataException("外部楽曲IDが不正または重複しています。");
            }
            if (entries.Count == 0) throw new InvalidDataException("空の外部マスターは反映できません。");
            dictionaries.Add(entries);
            sources.Add(new(Names[i], new Uri(BaseUri, Names[i]).AbsoluteUri, text, Hash(bytes[i])));
        }
        if (dictionaries.Skip(1).Any(d => !dictionaries[0].Keys.ToHashSet().SetEquals(d.Keys)))
            throw new InvalidDataException("楽曲情報・曲名・tagのID集合が一致しません。");
        var songs = new List<ExternalSong>();
        foreach (var (id, info) in dictionaries[0].OrderBy(p => p.Key))
        {
            if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("artist", out var artist) || artist.ValueKind != JsonValueKind.String ||
                !info.TryGetProperty("genre", out var genre) || genre.ValueKind != JsonValueKind.String ||
                !info.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out _))
                throw new InvalidDataException("楽曲情報の構造が不正です。");
            var title = RequiredString(dictionaries[1][id]);
            var tag = RequiredString(dictionaries[2][id]);
            if (tag != tag.Trim()) throw new InvalidDataException("tagの前後空白は許可しません。");
            var normalized = TitleNormalizer.Normalize(title);
            if (normalized.Length == 0) throw new InvalidDataException("正規化曲名が空です。");
            songs.Add(new(id, title, normalized, tag));
        }
        return new(songs, sources, acquiredAt);
    }

    private static string RequiredString(JsonElement element) => element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString()) ? element.GetString()! : throw new InvalidDataException("空でない文字列が必要です。");

    private static async Task<byte[]> ReadAsync(HttpClient client, string name, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, name));
            request.Headers.CacheControl = new() { NoCache = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (attempt < 1 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1));
                if (delay > TimeSpan.FromSeconds(10)) response.EnsureSuccessStatusCode();
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, token).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("入力サイズ上限を超えています。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaximumBytes) throw new InvalidDataException("入力サイズ上限を超えています。");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

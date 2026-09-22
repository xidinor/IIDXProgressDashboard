using System.Net.Http;
using System.Text;

namespace IIDXProgressDashboard.Difficulty;

/// <summary>テスト用HTTP境界を注入できる取得器。DBやFormへ依存しない。</summary>
public sealed class DifficultyTableProvider(HttpClient client,
    Func<DifficultyTableKind, CancellationToken, Task<DifficultySource>>? wikiBrowserFetcher = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, DifficultySource> cache = new(StringComparer.Ordinal);
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    private readonly DifficultyTableParser parser = new();

    public async Task<IReadOnlyList<DifficultyParseResult>> FetchAllAsync(CancellationToken token = default)
    {
        var results = new List<DifficultyParseResult>();
        // ☆12の2表は必ず同じ取得結果を共有する。片ゲージの解析不正はもう片方に波及させない。
        foreach (var kind in new[] { DifficultyTableKind.Sp11Normal, DifficultyTableKind.Sp11Hard, DifficultyTableKind.Sp12Normal })
        {
            var result = await FetchAsync(kind, token).ConfigureAwait(false);
            results.Add(result);
            if (kind == DifficultyTableKind.Sp12Normal)
                results.Add(result.Source != null && result.AcquisitionStatus != "FAILED"
                    ? await ParseAsync(DifficultyTableKind.Sp12Hard, result.Source, token).ConfigureAwait(false)
                    : result with { Table = DifficultyTableDefinition.Get(DifficultyTableKind.Sp12Hard) });
        }
        return results.AsReadOnly();
    }

    public Task<DifficultyParseResult> ParseAsync(DifficultyTableKind kind, DifficultySource source, CancellationToken token = default)
        => Task.Run(() => parser.Parse(kind, source, token), token);

    /// <summary>利用者が明示したWiki保存HTMLを読み取る。HTTP失敗からの自動切替やキャッシュ更新はしない。</summary>
    public async Task<DifficultyParseResult> ReadSavedHtmlAsync(DifficultyTableKind kind, string path, CancellationToken token = default)
    {
        var table = DifficultyTableDefinition.Get(kind);
        if (table.Level != 11) throw new ArgumentException("保存HTMLは☆11 Wiki専用です。", nameof(kind));
        DifficultySource? source = null;
        var started = DateTimeOffset.UtcNow;
        token.ThrowIfCancellationRequested();
        try
        {
            // 原本は読み取り専用。ファイル名・更新日時を出典URLやサイト取得日時として推測しない。
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > DifficultySource.MaxBytes) throw new InvalidDataException("入力上限8 MiBを超えています。");
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > DifficultySource.MaxBytes) throw new InvalidDataException("入力上限8 MiBを超えています。");
                buffer.Write(chunk, 0, read);
            }
            source = DifficultySource.FromUtf8(table.Url, buffer.ToArray(), "SAVED_HTML") with
            { StartedAt = started, CompletedAt = DateTimeOffset.UtcNow };
            return await ParseAsync(kind, source, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException)
        {
            // OS例外本文に含まれる個人の絶対パスを、永続化され得る診断へ持ち込まない。
            return new(table, source, Array.Empty<DifficultySourceRow>(),
                Array.AsReadOnly(new[] { new DifficultyDiagnostic("SAVED_HTML_READ_FAILED", $"保存HTMLを読み取れませんでした ({ex.GetType().Name})。") }),
                "FAILED", "NOT_RUN", false);
        }
    }

    public async Task<DifficultyParseResult> FetchAsync(DifficultyTableKind kind, CancellationToken token = default)
    {
        var table = DifficultyTableDefinition.Get(kind);
        DifficultySource? source = null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(table.Url, out var saved) && DateTimeOffset.UtcNow - saved.CompletedAt < TimeSpan.FromMinutes(15))
                source = saved;
            else if (table.Level == 11 && wikiBrowserFetcher != null)
            {
                // 呼出側が選んだ取得経路を使う。失敗時にHTTPや保存HTMLへ切り替えない。
                var delay = lastAttempt.AddSeconds(2) - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                lastAttempt = DateTimeOffset.UtcNow;
                source = await wikiBrowserFetcher(kind, token).ConfigureAwait(false);
            }
            else
            {
                // 手動連打でも最低2秒空ける。403/429は再試行せず、5xxのみ最大1回再試行する。
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var delay = lastAttempt.AddSeconds(2) - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                    lastAttempt = DateTimeOffset.UtcNow;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using var request = new HttpRequestMessage(HttpMethod.Get, table.Url);
                    request.Headers.UserAgent.ParseAdd("IIDXProgressDashboard/1.0");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if ((int)response.StatusCode >= 500 && attempt == 0) continue;
                    if (response.Content.Headers.ContentLength > DifficultySource.MaxBytes) throw new InvalidDataException("入力上限8 MiBを超えています。");
                    using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[16384];
                    int read;
                    while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        if (buffer.Length + read > DifficultySource.MaxBytes) throw new InvalidDataException("入力上限8 MiBを超えています。");
                        buffer.Write(chunk, 0, read);
                    }
                    source = DifficultySource.FromUtf8(table.Url, buffer.ToArray()) with
                    {
                        StartedAt = lastAttempt, CompletedAt = DateTimeOffset.UtcNow,
                        FinalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? table.Url,
                        HttpStatus = (int)response.StatusCode, ContentType = response.Content.Headers.ContentType?.ToString(),
                        ETag = response.Headers.ETag?.ToString(), LastModified = response.Content.Headers.LastModified?.ToString("O")
                    };
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (table.Level == 11 ? mediaType != "text/html" : mediaType != "application/json")
                        throw new InvalidDataException("Content-Typeが契約と一致しません。");
                    break;
                }
            }
            var parsed = await ParseAsync(kind, source!, token).ConfigureAwait(false);
            if (parsed.IsValid) cache[table.Url] = source!;
            return parsed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or DecoderFallbackException or OperationCanceledException)
        {
            // 古いキャッシュを最新取得成功として返さない。取得済みの応答は診断用に保持する。
            return new(table, source, Array.Empty<DifficultySourceRow>(),
                Array.AsReadOnly(new[] { new DifficultyDiagnostic("FETCH_FAILED", ex.Message) }), "FAILED", "NOT_RUN", false);
        }
        finally { gate.Release(); }
    }
}

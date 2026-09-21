using System.Net;
using System.Text;
using IIDXProgressDashboard.Difficulty;
using Xunit;

namespace IIDXProgressDashboard.Tests.Difficulty;

public sealed class DifficultyProviderTests
{
    private const string JsonRow = """{"name":"Synthetic (mix)","difficulty":"L","version":30,"d_value":3,"normal":"","hard":"個人差S+","n_value":0,"h_value":9.5,"extra":"preserved"}""";
    private static DifficultyParseResult Parse(DifficultyTableKind kind, string text)
        => new DifficultyTableParser().Parse(kind, DifficultySource.FromUtf8(DifficultyTableDefinition.Get(kind).Url, Encoding.UTF8.GetBytes(text)));

    // 公開曲表を転載せず、必須16節とH/A/Lを合成する。空節にもヘッダーが必要。
    internal static string Wiki(DifficultyTableKind kind, string difficulty = "L")
    {
        var table = DifficultyTableDefinition.Get(kind);
        string title = kind == DifficultyTableKind.Sp11Hard ? "☆11 (新ハード難易度表)" : "☆11 (新ノマゲ難易度表)";
        var result = new StringBuilder($"<html><body><h2 id='pagetitle'>{title}</h2><div id='wikibody'>");
        foreach (var rank in table.Ranks)
        {
            bool hasRow = rank.Code == "UNDECIDED";
            result.Append($"<h4>{rank.OriginalName} ({(hasRow ? 1 : 0)}曲)</h4><table><tr><td>ver</td><td>曲名</td><td>BPM(開幕)</td><td>notes</td><td>属性</td><td>☆</td><td colspan='2'>TexTage</td><td>レーダー</td><td>CR基準</td></tr>");
            if (hasRow)
            {
                string suffix = difficulty == "A" ? "" : $"({difficulty})", symbol = difficulty == "L" ? "X" : difficulty;
                result.Append($"<tr><td>30</td><td><s>Synthetic &amp; (mix){suffix}</s></td><td>180</td><td>1234</td><td>CN</td><td>NOTES</td><td><a href='https://textage.cc/score/30/synth.html?1{symbol}B00'>1P</a></td><td><a href='https://textage.cc/score/30/synth.html?2{symbol}B00'>2P</a></td><td>|||</td><td>地力S+</td></tr>");
            }
            result.Append("</table>");
        }
        return result.Append("</div><table><tr><td>outside</td></tr></table></body></html>").ToString();
    }

    [Theory]
    [InlineData(DifficultyTableKind.Sp11Normal, "H")]
    [InlineData(DifficultyTableKind.Sp11Hard, "A")]
    [InlineData(DifficultyTableKind.Sp11Normal, "L")]
    public void Wiki_preserves_chart_kind_raw_row_and_uses_section_rank(DifficultyTableKind kind, string difficulty)
    {
        var result = Parse(kind, Wiki(kind, difficulty));
        Assert.True(result.IsValid, string.Join(";", result.Diagnostics));
        Assert.Equal(16, result.Table.Ranks.Count);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Synthetic & (mix)", row.Candidate!.Title);
        Assert.Equal(difficulty, row.Candidate.Difficulty);
        Assert.Equal("UNDECIDED", row.Candidate.RankCode);
        Assert.Equal(1234, row.Candidate.Notes);
        Assert.Contains("<s>", row.Raw);
        Assert.Equal(row.RecordKey, Assert.Single(Parse(kind, Wiki(kind, difficulty)).Rows).RecordKey);
    }

    [Theory]
    [InlineData("地力F (0曲)", "新ランク (0曲)")]
    [InlineData("(1曲)", "(2曲)")]
    [InlineData("notes</td>", "other</td>")]
    [InlineData("?2XB00", "?2AB00")]
    [InlineData("?1XB00", "?DXB00")]
    [InlineData("1234</td>", "0</td>")]
    [InlineData("</html>", "")]
    [InlineData("新ノマゲ", "新ハード")]
    public void Wiki_rejects_incomplete_or_inconsistent_input(string from, string to)
    {
        var result = Parse(DifficultyTableKind.Sp11Normal, Wiki(DifficultyTableKind.Sp11Normal).Replace(from, to));
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Diagnostics);
        Assert.NotNull(result.Source);
    }

    [Fact]
    public void Json_separates_gauges_and_preserves_unknown_fields_and_null_optional_conditions()
    {
        var normal = Parse(DifficultyTableKind.Sp12Normal, "[" + JsonRow + "]");
        var hard = Parse(DifficultyTableKind.Sp12Hard, "[" + JsonRow + "]");
        Assert.True(normal.IsValid); Assert.True(hard.IsValid);
        Assert.Equal("UNRATED", normal.Rows[0].Candidate!.RankCode);
        Assert.Equal("KOJINSA_S_PLUS", hard.Rows[0].Candidate!.RankCode);
        Assert.Null(normal.Rows[0].Candidate!.Tag); Assert.Null(normal.Rows[0].Candidate!.Notes);
        Assert.Contains("preserved", normal.Rows[0].Raw);
        Assert.Equal(normal.Source!.Sha256, hard.Source!.Sha256);
        Assert.NotEqual(normal.Rows[0].RecordKey, hard.Rows[0].RecordKey);
        Assert.Equal(21, normal.Table.Ranks.Count);
    }

    [Theory]
    [InlineData("\"normal\":\"\"", "\"normal\":null")]
    [InlineData("\"normal\":\"\"", "\"normal\":\" \"")]
    [InlineData("\"normal\":\"\"", "\"normal\":\"unknown\"")]
    [InlineData("\"n_value\":0", "\"n_value\":1")]
    [InlineData("\"d_value\":3", "\"d_value\":2")]
    [InlineData("\"difficulty\":\"L\"", "\"difficulty\":\"DPA\"")]
    [InlineData("\"name\":", "\"missing\":")]
    public void Json_invalid_values_keep_original_row(string from, string to)
    {
        var result = Parse(DifficultyTableKind.Sp12Normal, "[" + JsonRow.Replace(from, to) + "]");
        Assert.False(result.IsValid); Assert.Single(result.Rows);
        Assert.Contains(result.Diagnostics, d => d.RowOrdinal == 1);
    }

    [Fact]
    public void Invalid_normal_does_not_poison_hard_and_duplicates_report_both_rows()
    {
        string row = JsonRow.Replace("\"normal\":\"\"", "\"normal\":null");
        Assert.False(Parse(DifficultyTableKind.Sp12Normal, "[" + row + "]").IsValid);
        Assert.True(Parse(DifficultyTableKind.Sp12Hard, "[" + row + "]").IsValid);
        var duplicate = Parse(DifficultyTableKind.Sp12Normal, $"[{JsonRow},{JsonRow}]");
        Assert.False(duplicate.IsValid);
        Assert.Equal(new int?[] { 1, 2 }, duplicate.Diagnostics.Where(d => d.Code == "DUPLICATE_SOURCE_KEY").Select(d => d.RowOrdinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[{")]
    [InlineData("<html><title>Just a moment...</title></html>")]
    public void Empty_broken_and_challenge_are_never_success(string text)
        => Assert.False(Parse(DifficultyTableKind.Sp12Normal, text).IsValid);

    [Fact]
    public void Unknown_section_preserves_its_rows_and_missing_section_is_not_an_empty_rank()
    {
        string wiki = Wiki(DifficultyTableKind.Sp11Normal);
        var unknown = Parse(DifficultyTableKind.Sp11Normal, wiki.Replace("未定 (1曲)", "未知 (1曲)"));
        Assert.False(unknown.IsValid);
        Assert.Single(unknown.Rows);
        Assert.Contains("Synthetic", unknown.Rows[0].Raw);
        var missing = Parse(DifficultyTableKind.Sp11Normal, wiki.Replace("<h4>地力F (0曲)</h4>", ""));
        Assert.False(missing.IsValid);
        Assert.Contains(missing.Diagnostics, d => d.Code == "MISSING_SECTION");
    }

    [Fact]
    public void Duplicate_wiki_rows_are_both_conflicts()
    {
        var wiki = Wiki(DifficultyTableKind.Sp11Normal);
        int start = wiki.IndexOf("<tr><td>30");
        int end = wiki.IndexOf("</tr>", start) + 5;
        var result = Parse(DifficultyTableKind.Sp11Normal, wiki.Insert(end, wiki[start..end]).Replace("(1曲)", "(2曲)"));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "DUPLICATE_SOURCE_KEY"));
    }

    [Fact]
    public void Bom_is_preserved_in_fingerprint_and_invalid_utf8_or_oversize_is_rejected()
    {
        var result = Parse(DifficultyTableKind.Sp12Normal, "\uFEFF[" + JsonRow + "]");
        Assert.True(result.IsValid);
        Assert.NotEqual(Parse(DifficultyTableKind.Sp12Normal, "[" + JsonRow + "]").Source!.Sha256, result.Source!.Sha256);
        Assert.Throws<DecoderFallbackException>(() => DifficultySource.FromUtf8("url", [0xff]));
        Assert.Throws<InvalidDataException>(() => DifficultySource.FromUtf8("url", new byte[DifficultySource.MaxBytes + 1]));
        var changed = result.Source! with { Content = "[]" };
        Assert.False(new DifficultyTableParser().Parse(DifficultyTableKind.Sp12Normal, changed).IsValid);
    }

    [Fact]
    public void Rank_dictionaries_are_table_specific_and_preserve_order()
    {
        var normal = DifficultyTableDefinition.Get(DifficultyTableKind.Sp11Normal).Ranks;
        var hard = DifficultyTableDefinition.Get(DifficultyTableKind.Sp11Hard).Ranks;
        Assert.Contains(normal, r => r.Code == "KOJINSA_E" && r.SortOrder == 15);
        Assert.DoesNotContain(hard, r => r.Code == "KOJINSA_E");
        Assert.Contains(hard, r => r.Code == "EXTREME_KOJINSA" && r.SortOrder == 110);
        Assert.DoesNotContain(normal, r => r.Code == "JIRIKI_A_PLUS");
    }

    [Fact]
    public async Task Http_success_with_wrong_media_type_is_failure_and_5xx_retries_are_bounded()
    {
        using var wrongClient = new HttpClient(new StubHandler(HttpStatusCode.OK, "[" + JsonRow + "]", "text/html"));
        Assert.Equal("FAILED", (await new DifficultyTableProvider(wrongClient).FetchAsync(DifficultyTableKind.Sp12Normal)).AcquisitionStatus);
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "unavailable", "text/html");
        using var retryClient = new HttpClient(handler);
        Assert.False((await new DifficultyTableProvider(retryClient).FetchAsync(DifficultyTableKind.Sp12Normal)).IsValid);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Provider_caches_valid_input_and_propagates_cancellation()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[" + JsonRow + "]", "application/json");
        using var client = new HttpClient(handler);
        var provider = new DifficultyTableProvider(client);
        Assert.True((await provider.FetchAsync(DifficultyTableKind.Sp12Normal)).IsValid);
        Assert.True((await provider.FetchAsync(DifficultyTableKind.Sp12Hard)).IsValid);
        Assert.Equal(1, handler.Count);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync(DifficultyTableKind.Sp12Normal, cancel.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Provider_does_not_retry_blocked_responses(HttpStatusCode status)
    {
        var handler = new StubHandler(status, "challenge", "text/html");
        using var client = new HttpClient(handler);
        var result = await new DifficultyTableProvider(client).FetchAsync(DifficultyTableKind.Sp11Normal);
        Assert.Equal("FAILED", result.AcquisitionStatus); Assert.Equal("NOT_RUN", result.ParseStatus);
        Assert.Equal((int)status, result.Source!.HttpStatus); Assert.Equal(1, handler.Count);
    }

    private sealed class StubHandler(HttpStatusCode status, string text, string contentType) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(text, Encoding.UTF8, contentType) });
        }
    }
}

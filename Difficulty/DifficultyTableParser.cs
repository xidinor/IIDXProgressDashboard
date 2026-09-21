using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace IIDXProgressDashboard.Difficulty;

/// <summary>ネットワーク・DBを持たず、元入力と全行の診断を返す。HTMLの自動補修だけでは完全としない。</summary>
public sealed class DifficultyTableParser
{
    public DifficultyParseResult Parse(DifficultyTableKind kind, DifficultySource source, CancellationToken cancellationToken = default)
    {
        var table = DifficultyTableDefinition.Get(kind);
        var rows = new List<DifficultySourceRow>();
        var diagnostics = new List<DifficultyDiagnostic>();
        cancellationToken.ThrowIfCancellationRequested();
        void Error(string code, string detail) => diagnostics.Add(new(code, detail));
        bool complete = false;
        try
        {
            if (Encoding.UTF8.GetByteCount(source.Content) > DifficultySource.MaxBytes)
                throw new InvalidDataException("入力上限8 MiBを超えています。");
            var verified = DifficultySource.FromUtf8(source.RequestedUrl, Encoding.UTF8.GetBytes(source.Content), source.InputKind);
            if (verified.Sha256 != source.Sha256 || verified.ByteLength != source.ByteLength)
                throw new InvalidDataException("入力本文とハッシュ・サイズが一致しません。");
            if (source.RequestedUrl != table.Url || source.FinalUrl != table.Url)
                throw new InvalidDataException("対象URLが表の契約と一致しません。");
            if (source.HttpStatus is not (null or 200)) throw new InvalidDataException("HTTP取得失敗です。");
            if (string.IsNullOrWhiteSpace(source.Content)) throw new InvalidDataException("入力が空です。");
            if (source.Content.Contains("_cf_chl_opt", StringComparison.Ordinal) || source.Content.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("チャレンジ画面を検出しました。");
            if (table.Level == 12) ParseJson(table, source, rows, diagnostics, cancellationToken);
            else ParseWiki(table, source, rows, diagnostics, cancellationToken);
            complete = true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or DecoderFallbackException)
        { Error("INVALID_SOURCE", ex.Message); }
        // 同一評価でも重複は競合。全関係行を示し、先勝ち・後勝ちにはしない。
        foreach (var group in rows.Where(r => r.SourceKey != null).GroupBy(r => r.SourceKey, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var row in group) diagnostics.Add(new("DUPLICATE_SOURCE_KEY", "同じ譜面の元行が複数あります。", row.Ordinal));
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count == 0) Error("EMPTY_TABLE", "曲行がありません。");
        bool valid = complete && diagnostics.Count == 0;
        bool structureComplete = complete && !diagnostics.Any(d => d.RowOrdinal == null);
        return new(table, source, rows.AsReadOnly(), diagnostics.AsReadOnly(), structureComplete ? "COMPLETE" : "INCOMPLETE", valid ? "VALID" : "INVALID", complete);
    }

    private static void ParseJson(DifficultyTableDefinition table, DifficultySource source, List<DifficultySourceRow> rows,
        List<DifficultyDiagnostic> diagnostics, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(source.Content.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("JSON rootは配列である必要があります。");
        string gauge = table.Gauge == "HARD" ? "hard" : "normal", valueKey = table.Gauge == "HARD" ? "h_value" : "n_value";
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (rows.Count >= 20000) throw new InvalidDataException("曲行上限20,000を超えています。");
            int ordinal = rows.Count + 1;
            string? title = null, rankText = null, key = null;
            DifficultyCandidate? candidate = null;
            try
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("曲行がオブジェクトではありません。");
                if (item.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) throw new InvalidDataException("JSONキーが重複しています。");
                title = String(item, "name");
                if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("曲名が空です。");
                var difficulty = String(item, "difficulty");
                if (difficulty is not ("H" or "A" or "L")) throw new InvalidDataException("未知の譜面種別です。");
                key = JsonSerializer.Serialize(new[] { title, "SP", difficulty });
                // versionは出典情報のみ。曲や譜面の識別には使わない。
                _ = Number(item, "version");
                if (Number(item, "d_value") != (difficulty == "H" ? 1 : difficulty == "A" ? 2 : 3))
                    throw new InvalidDataException("d_valueと譜面種別が矛盾しています。");
                rankText = String(item, gauge);
                var rank = table.Ranks.SingleOrDefault(r => r.OriginalName == rankText)
                    ?? throw new InvalidDataException("未知の評価です。");
                decimal expected = rank.Code == "UNRATED" ? 0 : rank.SortOrder / 10m;
                if (Number(item, valueKey) != expected) throw new InvalidDataException("評価文字列と数値が矛盾しています。");
                candidate = new(title, null, difficulty, null, rank.Code);
            }
            catch (InvalidDataException ex) { diagnostics.Add(new("INVALID_ROW", ex.Message, ordinal)); }
            rows.Add(new(ordinal, item.GetRawText(), null, rankText, title, key, candidate, DifficultyParseResult.RowKey(table, source, ordinal)));
        }
    }
    private static string String(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!
            : throw new InvalidDataException($"{name}が文字列ではありません。");
    private static decimal Number(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number
            : throw new InvalidDataException($"{name}が数値ではありません。");

    private static void ParseWiki(DifficultyTableDefinition table, DifficultySource source, List<DifficultySourceRow> rows,
        List<DifficultyDiagnostic> diagnostics, CancellationToken token)
    {
        // 静的HtmlParserのみ使用。スクリプト実行・外部リソースの取得は有効化しない。
        using var doc = new HtmlParser().ParseDocument(source.Content);
        string expectedTitle = table.Gauge == "HARD" ? "☆11 (新ハード難易度表)" : "☆11 (新ノマゲ難易度表)";
        if (doc.QuerySelectorAll("#pagetitle").Length != 1 || doc.QuerySelector("#pagetitle")!.TextContent.Trim() != expectedTitle
            || doc.QuerySelectorAll("#wikibody").Length != 1)
            throw new InvalidDataException("対象ページの題名または本文が不正です。");
        if (!source.Content.TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HTML終端がありません。途中入力は受理しません。");
        // HTML5の省略タグ補修で切れた曲行を正常化しない。採用Wikiは明示終端を持つ。
        foreach (string tag in new[] { "table", "tr", "td", "h4" })
            if (Regex.Matches(source.Content, $@"<{tag}(\s|>)", RegexOptions.IgnoreCase).Count
                != Regex.Matches(source.Content, $@"</{tag}\s*>", RegexOptions.IgnoreCase).Count)
                throw new InvalidDataException($"{tag}の開始・終了数が一致しません。");
        var body = doc.QuerySelector("#wikibody")!;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? heading = null;
        DifficultyRank? rank = null;
        int expected = 0;
        bool awaitingTable = false;
        foreach (var element in body.QuerySelectorAll("h4, table"))
        {
            token.ThrowIfCancellationRequested();
            if (element.LocalName == "h4")
            {
                if (awaitingTable) diagnostics.Add(new("MISSING_TABLE", heading!));
                heading = element.TextContent.Trim();
                if (heading.Length > 256) throw new InvalidDataException("ランク見出しが長すぎます。");
                var match = Regex.Match(heading, @"^(.*?)\s*\(([0-9]+)曲\)$");
                rank = match.Success ? table.Ranks.SingleOrDefault(r => r.OriginalName == match.Groups[1].Value.Trim()) : null;
                if (rank == null || !int.TryParse(match.Groups[2].Value, out expected))
                { diagnostics.Add(new("UNKNOWN_SECTION", heading)); rank = null; awaitingTable = true; expected = -1; continue; }
                if (!seen.Add(rank.Code)) diagnostics.Add(new("DUPLICATE_SECTION", heading));
                awaitingTable = true;
                continue;
            }
            if (heading == null) continue;
            if (!awaitingTable) { diagnostics.Add(new("UNEXPECTED_TABLE", "ランク節に複数の表があります。")); continue; }
            awaitingTable = false;
            var trs = element.QuerySelectorAll("tr").Where(r => r.Closest("table") == element).ToArray();
            string[] headers = ["ver", "曲名", "BPM(開幕)", "notes", "属性", "☆", "TexTage", "TexTage", "レーダー", "CR基準"];
            var expanded = new List<string>();
            if (trs.Length > 0)
                foreach (var cell in trs[0].Children)
                {
                    var span = cell.GetAttribute("colspan") ?? "1";
                    if (!int.TryParse(span, out var count) || count is < 1 or > 2 || cell.HasAttribute("rowspan"))
                    { diagnostics.Add(new("INVALID_HEADER", heading!)); continue; }
                    expanded.AddRange(Enumerable.Repeat(cell.TextContent.Trim(), count));
                }
            if (!expanded.SequenceEqual(headers)) diagnostics.Add(new("INVALID_HEADER", heading!));
            if (expected >= 0 && trs.Length - 1 != expected) diagnostics.Add(new("SECTION_COUNT", $"{heading}: 実行数={Math.Max(0, trs.Length - 1)}"));
            foreach (var tr in trs.Skip(1))
            {
                token.ThrowIfCancellationRequested();
                if (rows.Count >= 20000) throw new InvalidDataException("曲行上限20,000を超えています。");
                int ordinal = rows.Count + 1;
                string? title = null, key = null;
                DifficultyCandidate? candidate = null;
                try
                {
                    if (rank == null) throw new InvalidDataException("未知ランク節の曲行です。");
                    var cells = tr.Children.ToArray();
                    if (cells.Length != 10 || cells.Any(c => c.LocalName != "td" || c.HasAttribute("colspan") || c.HasAttribute("rowspan")))
                        throw new InvalidDataException("曲行の列構造が不正です。");
                    title = cells[1].TextContent.Trim();
                    string difficulty = title.EndsWith("(H)", StringComparison.Ordinal) ? "H" : title.EndsWith("(L)", StringComparison.Ordinal) ? "L" : "A";
                    if (difficulty == "A" && Regex.IsMatch(title, @"\([A-Z]\)$")) throw new InvalidDataException("未知の末尾譜面記号です。");
                    string name = difficulty == "A" ? title : title[..^3].TrimEnd();
                    if (name.Length == 0) throw new InvalidDataException("曲名が空です。");
                    var first = Link(cells[6], "1", difficulty);
                    var second = Link(cells[7], "2", difficulty);
                    if (first != second) throw new InvalidDataException("1P/2Pのtagが矛盾しています。");
                    key = JsonSerializer.Serialize(new[] { first, "SP", difficulty });
                    if (!int.TryParse(cells[3].TextContent.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var notes) || notes <= 0)
                        throw new InvalidDataException("notesは正の整数が必要です。");
                    candidate = new(name, first, difficulty, notes, rank.Code);
                }
                catch (InvalidDataException ex) { diagnostics.Add(new("INVALID_ROW", ex.Message, ordinal)); }
                rows.Add(new(ordinal, tr.OuterHtml, heading, rank?.OriginalName, title, key, candidate, DifficultyParseResult.RowKey(table, source, ordinal)));
            }
        }
        if (awaitingTable) diagnostics.Add(new("MISSING_TABLE", heading!));
        foreach (var missing in table.Ranks.Where(r => !seen.Contains(r.Code))) diagnostics.Add(new("MISSING_SECTION", missing.OriginalName));
    }
    private static string Link(IElement cell, string side, string difficulty)
    {
        var links = cell.QuerySelectorAll("a[href]");
        if (links.Length != 1 || !Uri.TryCreate(links[0].GetAttribute("href"), UriKind.Absolute, out var uri)
            || uri.Host != "textage.cc" || uri.Scheme is not ("https" or "http") || uri.Fragment.Length != 0)
            throw new InvalidDataException("TexTageリンクが不正です。");
        string symbol = difficulty == "L" ? "X" : difficulty;
        if (uri.Query != $"?{side}{symbol}B00") throw new InvalidDataException("TexTageのSP・譜面・level指定が矛盾または未知形式です。");
        var path = Regex.Match(uri.AbsolutePath, @"^/score/[^/]+/([A-Za-z0-9_]+)\.html$");
        return path.Success ? path.Groups[1].Value : throw new InvalidDataException("TexTageのtagが不正です。");
    }
}

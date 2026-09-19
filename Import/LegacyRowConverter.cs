using System.Globalization;
using System.Text.Json;
using IIDXProgressDashboard.Matching;

namespace IIDXProgressDashboard.Import;

/// <summary>旧行の値を検証する。元行は変換結果とは別に保持し、不正値を既定値へ置換しない。</summary>
internal static class LegacyRowConverter
{
    internal static LegacyConvertedRow Convert(IReadOnlyDictionary<string, object?> row)
    {
        var issues = new List<ChartResolutionIssue>();
        string? Text(string name, bool required = false)
        {
            var value = row[name];
            if (value is string text && (!required || !string.IsNullOrWhiteSpace(text))) return text;
            if (value is not null || required) issues.Add(new("INVALID_" + name.ToUpperInvariant(), name + "の文字列が不正です。"));
            return null;
        }
        int? Number(string name, int minimum, int maximum, bool required = false, bool textAllowed = false)
        {
            var value = row[name];
            if (value is null && !required) return null;
            long parsed;
            if (value is long integer) parsed = integer;
            else if (textAllowed && value is string text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)) parsed = numeric;
            else { issues.Add(new("INVALID_" + name.ToUpperInvariant(), name + "は整数で指定してください。")); return null; }
            if (parsed < minimum || parsed > maximum)
            { issues.Add(new("INVALID_" + name.ToUpperInvariant(), name + "が範囲外です。")); return null; }
            return (int)parsed;
        }

        var title = Text("song_name", true);
        var difficulty = Text("difficulty_type", true);
        var level = Number("level", 1, 12, textAllowed: true);
        var notes = Number("total_notes", 0, int.MaxValue);
        var score = Number("score", 0, int.MaxValue, true);
        var bp = Number("miss_count", 0, int.MaxValue);
        var lampText = Text("clear_type", true);
        int? lamp = lampText switch
        {
            "NO PLAY" => 0, "FAILED" => 1, "A-CLEAR" => 2, "E-CLEAR" => 3,
            "CLEAR" => 4, "H-CLEAR" => 5, "EXH-CLEAR" => 6, "F-COMBO" => 7, _ => null
        };
        if (lamp is null) issues.Add(new("INVALID_LAMP", "旧ランプ8表記のいずれにも一致しません。"));
        var date = Text("played_at", true);
        string? utc = null;
        if (DateTime.TryParseExact(date, "yyyy-MM-dd-HH-mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            // 秒の00は保存形式の埋め値。元の分精度はraw_dataのmetadataに明示する。
            try { utc = new DateTimeOffset(local, TimeSpan.FromHours(9)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { issues.Add(new("INVALID_PLAYED_AT", "UTCへの変換結果が日時の範囲外です。")); }
        }
        else issues.Add(new("INVALID_PLAYED_AT", "旧日時はJSTのyyyy-MM-dd-HH-mmで指定してください。"));
        Text("played_option");
        Text("original_data");
        var tag = row.ContainsKey("song_tag") ? Text("song_tag") : null;
        if (string.IsNullOrWhiteSpace(tag)) tag = null;
        return new(new(title, difficulty, Tag: tag, Level: level, TotalNotes: notes, SourceName: "LEGACY_INFINITAS_LOG"),
            utc, lamp, score, bp, issues);
    }

    internal static string Serialize(IReadOnlyDictionary<string, object?> row) => JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        playedAtPrecision = "minute",
        sourceTimeZone = "+09:00",
        // SQLiteは列宣言と異なる型も格納できる。BLOBと同じbase64文字列等を同一内容としない。
        storageTypes = row.ToDictionary(p => p.Key, p => p.Value switch
        { null => "null", long => "integer", double => "real", byte[] => "blob", _ => "text" }),
        row = row.ToDictionary(p => p.Key, p => p.Value is double number && !double.IsFinite(number)
            ? (object)new { sqliteReal = number.ToString(CultureInfo.InvariantCulture) } : p.Value)
    });
}

internal sealed record LegacyConvertedRow(ChartResolutionRequest Request, string? PlayedAt, int? Lamp,
    int? Score, int? MissCount, IReadOnlyList<ChartResolutionIssue> Issues);

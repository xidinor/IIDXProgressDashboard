using System.Globalization;
using IIDXProgressDashboard.Matching;

namespace IIDXProgressDashboard.Import;

public enum RefluxTimeMode { Utc, Local }

/// <summary>Localでは出力したPCのタイムゾーンを明示し、取込PCの設定から推測しない。</summary>
public sealed record RefluxImportOptions(RefluxTimeMode TimeMode = RefluxTimeMode.Utc, string? TimeZoneId = null)
{
    internal TimeZoneInfo GetTimeZone()
    {
        if (TimeMode == RefluxTimeMode.Utc && TimeZoneId is null) return TimeZoneInfo.Utc;
        if (TimeMode != RefluxTimeMode.Local || string.IsNullOrWhiteSpace(TimeZoneId))
            throw new ArgumentException("UTCはTimeZoneIdなし、Localは明示的なTimeZoneIdを指定してください。");
        return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }
}

public sealed record RefluxConvertedRow(ChartResolutionRequest Request, string? PlayedAt, int? Lamp,
    IReadOnlyDictionary<string, int?> Numbers, IReadOnlyDictionary<string, string?> Text,
    IReadOnlyList<ChartResolutionIssue> Issues);

/// <summary>上流TSVの値を変換する。未知値・欠損スコアを既定値で補完しない。</summary>
public static class RefluxRowConverter
{
    public static RefluxConvertedRow Convert(RefluxSessionRow row, RefluxImportOptions options)
    {
        var zone = options.GetTimeZone();
        var issues = new List<ChartResolutionIssue>();
        string? Value(string key) => row.Values.TryGetValue(key, out var value) ? value : null;
        void Invalid(string key) => issues.Add(new("INVALID_" + key.ToUpperInvariant(), $"{key}の値が不正です。"));
        var numbers = new Dictionary<string, int?>();
        foreach (var key in new[] { "exscore", "misscount", "notecount", "level", "gaugepercent", "pgreat", "great", "good", "bad", "poor", "combobreak", "fast", "slow" })
        {
            var raw = Value(key);
            int? value = null;
            if (string.IsNullOrEmpty(raw) || key == "misscount" && raw == "-")
            {
                if (key == "exscore") Invalid(key);
            }
            else if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                     key == "level" && parsed is < 1 or > 12 || key == "gaugepercent" && parsed > 100)
                Invalid(key);
            else value = parsed;
            numbers.Add(key, value);
        }
        if (row.ErrorCode is not null) issues.Add(new(row.ErrorCode, "ヘッダーとフィールド数が一致しません。"));
        if (string.IsNullOrWhiteSpace(Value("title"))) Invalid("title");
        var difficulty = Value("difficulty");
        if (difficulty is null || difficulty.Length != 3 ||
            difficulty[..2] is not ("SP" or "DP") || !"BNHAL".Contains(difficulty[2])) Invalid("difficulty");
        int? lamp = Value("lamp") switch
        {
            "NP" => 0, "F" => 1, "AC" => 2, "EC" => 3, "NC" => 4,
            "HC" => 5, "EX" => 6, "FC" or "PFC" => 7, _ => null
        };
        if (lamp is null) Invalid("lamp");
        var text = new Dictionary<string, string?>();
        foreach (var key in new[] { "playtype", "style", "style2", "gauge", "assist", "range" })
            text.Add(key, string.IsNullOrEmpty(Value(key)) ? null : Value(key));
        var side = text["playtype"];
        if (side is not (null or "P1" or "P2" or "DP") ||
            side is not null && difficulty?.Length == 3 && (side == "DP") != difficulty.StartsWith("DP", StringComparison.Ordinal))
            Invalid("playtype");
        string? playedAt = null;
        if (!DateTime.TryParseExact(Value("date"), "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)) Invalid("date");
        else
        {
            date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            // 夏時間の二通りの解釈や存在しない時刻は、自動選択せず元行を保留する。
            if (zone.IsInvalidTime(date) || zone.IsAmbiguousTime(date)) Invalid("date");
            else
            {
                var utcTicks = date.Ticks - zone.GetUtcOffset(date).Ticks;
                if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks) Invalid("date");
                else playedAt = new DateTime(utcTicks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
            }
        }
        if (numbers["notecount"] is int notes && numbers["exscore"] is int score && score > (long)notes * 2)
            Invalid("exscore");
        return new(new(Value("title"), difficulty, Level: numbers["level"], TotalNotes: numbers["notecount"],
            SourceName: "REFLUX_SESSION_TSV"), playedAt, lamp, numbers, text, issues);
    }
}

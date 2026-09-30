using System.Globalization;
using System.Text.Json;

namespace IIDXProgressDashboard.Dashboard;

public sealed record HistoryPoint(long PlayId, long ChartId, int Number, string PlayedAt, int Lamp,
    int Score, int? MissCount, bool MinutePrecision, string Options)
{
    public string LocalDate => DateTimeOffset.Parse(PlayedAt, CultureInfo.InvariantCulture)
        .ToLocalTime().ToString(MinutePrecision ? "yyyy/MM/dd HH:mm" : "yyyy/MM/dd HH:mm:ss");
}

public sealed record ChartView(long ChartId, string Title, string Style, string Difficulty, int? Level,
    int? Notes, bool Active, IReadOnlyList<HistoryPoint> History)
{
    public HistoryPoint? Latest => History.LastOrDefault();
    public int? BestLamp => History.Count == 0 ? null : History.Max(p => p.Lamp);
    public int? BestScore => History.Count == 0 ? null : History.Max(p => p.Score);
    public int? MinimumBp => History.Select(p => p.MissCount).Min();
    public string Label => $"{Title} [{Style}{Difficulty}]";
}
public sealed record RankView(string Code, string Name, int Order);
public sealed record TableView(long Id, string Code, string Name, IReadOnlyList<RankView> Ranks,
    IReadOnlyDictionary<long, string> Entries, IReadOnlySet<long> Retained);
public sealed record DashboardSnapshot(IReadOnlyList<ChartView> Charts, IReadOnlyList<TableView> Tables, int Pending);

public static class DisplayValues
{
    public static string Lamp(int? value) => value switch
    {
        null => "未プレイ", 0 => "NO PLAY（履歴あり）", 1 => "FAILED", 2 => "ASSIST CLEAR",
        3 => "EASY CLEAR", 4 => "CLEAR", 5 => "HARD CLEAR", 6 => "EX HARD CLEAR", 7 => "FULL COMBO",
        _ => "不正なランプ"
    };

    public static (string DjLevel, string Rate) Score(int score, int? notes)
    {
        if (notes is null or <= 0) return ("計算不能：マスターデータ不完全", "—");
        if (score < 0 || score > notes.Value * 2L) return ("計算不能：現行Notesの範囲外", "—");
        // 判定は整数、表示だけを四捨五入する。表示の丸めでランクを繰り上げない。
        string[] levels = ["F", "E", "D", "C", "B", "A", "AA", "AAA"];
        var level = "F";
        for (int k = 8; k >= 2; k--)
            if (score * 9L >= notes.Value * 2L * k) { level = levels[k - 1]; break; }
        var percent = Math.Round(score * 100m / (notes.Value * 2L), 2, MidpointRounding.AwayFromZero);
        return (level, percent.ToString("F2", CultureInfo.InvariantCulture) + "%");
    }

    public static HistoryPoint[] Visible(IReadOnlyList<HistoryPoint> history, bool excludeMissingBp, bool hideRepeated = false)
    {
        IEnumerable<HistoryPoint> points = hideRepeated ? WithoutRepeated(history) : history;
        return points.Where(p => !excludeMissingBp || p.MissCount.HasValue).ToArray();
    }

    private static IEnumerable<HistoryPoint> WithoutRepeated(IReadOnlyList<HistoryPoint> history)
    {
        // 取得済み履歴だけを比較する。同じ日の同条件の連続記録は最初の1件を残し、DBは変更しない。
        var previous = new Dictionary<(long ChartId, DateOnly Day, int Lamp, int Score, int? Bp, string Options), DateTimeOffset>();
        var kept = new List<HistoryPoint>();
        foreach (var point in history.OrderBy(p => p.PlayedAt, StringComparer.Ordinal).ThenBy(p => p.PlayId))
        {
            var time = DateTimeOffset.Parse(point.PlayedAt, CultureInfo.InvariantCulture);
            var key = (point.ChartId, DateOnly.FromDateTime(time.ToLocalTime().DateTime), point.Lamp, point.Score, point.MissCount, point.Options);
            var repeated = previous.TryGetValue(key, out var last) && time - last <= TimeSpan.FromMinutes(2);
            previous[key] = time;
            if (!repeated) kept.Add(point);
        }
        return kept.OrderBy(p => p.Number);
    }

    internal static bool IsMinute(string? raw)
    {
        if (raw is null) return false;
        using var json = JsonDocument.Parse(raw);
        return json.RootElement.TryGetProperty("playedAtPrecision", out var value) && value.GetString() == "minute";
    }

    // 旧履歴のオプションは専用列に移されていないため、保全した元行から表示時だけ読む。
    internal static string Options(string? style1, string? style2, string? raw)
    {
        if (raw is not null)
        {
            using var json = JsonDocument.Parse(raw);
            if (json.RootElement.TryGetProperty("row", out var row) &&
                row.TryGetProperty("played_option", out var option) && option.ValueKind == JsonValueKind.String)
                return string.IsNullOrWhiteSpace(option.GetString()) ? "—" : option.GetString()!;
        }
        var parts = new[] { style1, style2 }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var text = string.Join(" / ", parts);
        return text.Length == 0 ? "—" : text;
    }
}

namespace IIDXProgressDashboard.Matching;

public enum SongTitleMatchStatus { NotFound, Unique, Ambiguous }

/// <summary>SourceNameがnullなら正式タイトル、それ以外はaliasの出典。</summary>
public sealed record SongTitleEvidence(string Tag, string OriginalTitle, string? SourceName);

/// <summary>曲候補のみを返す。譜面条件によるchart_idの確定は後続のChartResolverが担当する。</summary>
public sealed class SongTitleMatch
{
    public IReadOnlyList<SongTitleEvidence> Evidence { get; }
    public IReadOnlyList<string> Tags { get; }
    public SongTitleMatchStatus Status => Tags.Count switch
    {
        0 => SongTitleMatchStatus.NotFound,
        1 => SongTitleMatchStatus.Unique,
        _ => SongTitleMatchStatus.Ambiguous
    };

    internal SongTitleMatch(IEnumerable<SongTitleEvidence> evidence)
    {
        Evidence = Array.AsReadOnly(evidence.OrderBy(e => e.Tag, StringComparer.Ordinal)
            .ThenBy(e => e.SourceName, StringComparer.Ordinal).ToArray());
        Tags = Array.AsReadOnly(Evidence.Select(e => e.Tag).Distinct(StringComparer.Ordinal).ToArray());
    }
}

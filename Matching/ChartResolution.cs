namespace IIDXProgressDashboard.Matching;

/// <summary>DifficultyはSPA等、またはPlayStyleとB/N/H/A/L（旧長名も可）。nullは任意情報の未取得。</summary>
public sealed record ChartResolutionRequest(string? Title, string? Difficulty, string? PlayStyle = null,
    string? Tag = null, int? Level = null, int? TotalNotes = null, string? SourceName = null,
    long? ExternalSongId = null, string? ExternalSourceName = null);

/// <summary>曲名から得た候補はTITLE_DERIVED、入力にあるIDはDIRECT_ID。独立した証拠として混同しない。</summary>
public sealed record ExternalSongEvidence(string SourceName, long ExternalSongId, string Tag, string Title, string MatchKind);

public sealed record ChartCandidate(long ChartId, string Tag, string Title, string PlayStyle,
    string Difficulty, int? Level, int? TotalNotes, bool SongIsActive, bool ChartIsActive);

/// <summary>Codeはunresolved_imports.reason_code、Detailはreason_detailへ渡す安定した契約。</summary>
public sealed record ChartResolutionIssue(string Code, string Detail);

public sealed class ChartResolution
{
    public ChartResolutionRequest Request { get; }
    public long? ChartId { get; }
    public bool IsResolved => ChartId.HasValue;
    public IReadOnlyList<ChartResolutionIssue> Issues { get; }
    public IReadOnlyList<ChartCandidate> Candidates { get; }
    public IReadOnlyList<SongTitleEvidence> TitleEvidence { get; }
    public IReadOnlyList<ExternalSongEvidence> ExternalEvidence { get; }

    internal ChartResolution(ChartResolutionRequest request, long? chartId,
        IEnumerable<ChartResolutionIssue> issues, IEnumerable<ChartCandidate> candidates,
        IEnumerable<SongTitleEvidence> evidence, IEnumerable<ExternalSongEvidence>? externalEvidence = null)
    {
        Request = request;
        ChartId = chartId;
        Issues = Array.AsReadOnly(issues.ToArray());
        Candidates = Array.AsReadOnly(candidates.ToArray());
        TitleEvidence = Array.AsReadOnly(evidence.ToArray());
        ExternalEvidence = Array.AsReadOnly((externalEvidence ?? []).ToArray());
    }
}

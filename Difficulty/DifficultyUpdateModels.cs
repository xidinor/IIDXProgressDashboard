using System.Text.Json;

namespace IIDXProgressDashboard.Difficulty;

public sealed record DifficultyChangeCounts(int Added, int Updated, int Unchanged);
public sealed record DifficultyMissingRow(string SourceKey, string? Title, string? Difficulty, string? RankCode, long? ChartId);
public sealed record DifficultyUpdateResult(long ImportRunId, string BackupPath, string Status, string ApplyStatus,
    DifficultyChangeCounts Counts, int Unresolved, int Missing, int Retained);

/// <summary>原本・基準DB・消失差分に束縛した変更案。入力は独立コピー、照合行は変更不能。</summary>
public sealed class DifficultyUpdatePlan
{
    internal string DatabasePath { get; }
    internal string Revision { get; }
    internal string SnapshotJson { get; }
    internal DifficultyMatchingResult Matching { get; }
    internal DifficultyUpdateState? Previous { get; }
    internal long? PreviousRunId { get; }
    public Guid PlanId { get; } = Guid.NewGuid();
    public Guid? BatchId { get; }
    public string TableCode { get; }
    public IReadOnlyList<DifficultyMatchedRow> Rows => Matching.Rows;
    public IReadOnlyList<DifficultyMissingRow> MissingRows { get; }
    public DifficultyChangeCounts PlannedCounts { get; }
    public bool CanApply { get; }
    public bool RequiresMissingConfirmation => MissingRows.Count > 0;
    public DifficultyParseResult Input
    {
        get
        {
            using var doc = JsonDocument.Parse(SnapshotJson);
            return doc.RootElement.GetProperty("input").Deserialize<DifficultyParseResult>(DifficultyMatchingAudit.JsonOptions)!;
        }
    }

    internal DifficultyUpdatePlan(string path, string revision, DifficultyMatchingResult matching,
        DifficultyUpdateState? previous, long? previousRun, DifficultyMissingRow[] missing,
        DifficultyChangeCounts counts, Guid? batchId)
    {
        DatabasePath = path; Revision = revision; Previous = previous; PreviousRunId = previousRun;
        // 外部から渡る診断リストもコピーし、呼出側の変更が計画へ波及しないようにする。
        Matching = new(matching.Input with
        {
            Table = DifficultyTableDefinition.Get(matching.Input.Table.Kind),
            Rows = Array.AsReadOnly(matching.Input.Rows.ToArray()),
            Diagnostics = Array.AsReadOnly(matching.Input.Diagnostics.ToArray())
        }, matching.Rows);
        SnapshotJson = DifficultyMatchingAudit.CreateSnapshot(Matching);
        TableCode = matching.Input.Table.Code; CanApply = matching.CanPrepareUpdate;
        MissingRows = Array.AsReadOnly(missing); PlannedCounts = counts; BatchId = batchId;
    }
}

// 所有状態は終了ログと同時commitする。保持集合も残し、古い評価と今回確認した評価を区別する。
internal sealed record DifficultyUpdateState(int Version, string TableCode, long AcceptedGenerationId,
    long? ParentGenerationId, string InputSha256, string InputKind, int RankDictionaryVersion,
    string[] SourceKeys, Dictionary<string, long> SourceChartIds, long[] OwnedChartIds,
    string[] MissingSourceKeys, long[] RetainedChartIds, string TableFingerprint);

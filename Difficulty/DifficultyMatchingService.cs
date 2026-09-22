using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Difficulty;

public enum DifficultyRowStatus { Resolved, Unresolved, Invalid, Conflict, NotProcessed }

public sealed record DifficultyMatchedRow(DifficultySourceRow Source, DifficultyRowStatus Status,
    ChartResolution? Resolution, IReadOnlyList<DifficultyDiagnostic> Diagnostics)
{
    // 競合行の候補IDを登録用IDと取り違えない。
    public long? ChartId => Status == DifficultyRowStatus.Resolved ? Resolution?.ChartId : null;
}

public sealed class DifficultyMatchingResult
{
    public DifficultyParseResult Input { get; }
    public IReadOnlyList<DifficultyMatchedRow> Rows { get; }
    public bool CanPrepareUpdate => Input.IsValid && Input.ReadComplete &&
        Rows.All(r => r.Status is DifficultyRowStatus.Resolved or DifficultyRowStatus.Unresolved);
    public int Count(DifficultyRowStatus status) => Rows.Count(r => r.Status == status);

    internal DifficultyMatchingResult(DifficultyParseResult input, IEnumerable<DifficultyMatchedRow> rows)
    {
        Input = input;
        Rows = Array.AsReadOnly(rows.ToArray());
    }
}

/// <summary>表全体を同じDB snapshotで照合する。表更新・alias生成・未解決の自動解決は行わない。</summary>
public sealed class DifficultyMatchingService(DatabaseInitializer database)
{
    public Task<DifficultyMatchingResult> ResolveAsync(DifficultyParseResult input, CancellationToken token = default)
        => Task.Run(() => Resolve(input, token), token);

    public DifficultyMatchingResult Resolve(DifficultyParseResult input, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = database.OpenConnection();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';";
            if ((long)check.ExecuteScalar()! == 0) throw new InvalidOperationException("先に出力用DBを初期化してください。");
        }
        new MigrationRunner().Run(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        return Resolve(input, connection, transaction, token);
    }

    // 5-5の反映側は書込トランザクション内で再照合する。単独照合結果は書込許可ではない。
    internal static DifficultyMatchingResult Resolve(DifficultyParseResult input, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        token.ThrowIfCancellationRequested();
        // 公開recordの候補やIsValidを信用せず、保持した原本から正規のParser結果を再構築する。
        if (input.Source is not null)
            input = new DifficultyTableParser().Parse(input.Table.Kind, input.Source, token);
        else if (input.Rows.Count != 0 || input.IsValid)
            throw new InvalidDataException("元入力のない候補は照合できません。");
        var rows = new List<DifficultyMatchedRow>();
        foreach (var row in input.Rows)
        {
            token.ThrowIfCancellationRequested();
            var diagnostics = input.Diagnostics.Where(d => d.RowOrdinal == row.Ordinal).ToList();
            ChartResolution? resolution = null;
            DifficultyRowStatus status;
            if (diagnostics.Any(d => d.Code == "DUPLICATE_SOURCE_KEY")) status = DifficultyRowStatus.Conflict;
            else if (diagnostics.Count > 0 || row.Candidate is null) status = DifficultyRowStatus.Invalid;
            else if (input.AcquisitionStatus != "COMPLETE" || !input.ReadComplete) status = DifficultyRowStatus.NotProcessed;
            else
            {
                var candidate = row.Candidate;
                resolution = ChartResolver.Resolve(new(candidate.Title, candidate.Difficulty, candidate.PlayStyle,
                    candidate.Tag, input.Table.Level, candidate.Notes, input.Table.SourceName), connection, transaction);
                diagnostics.AddRange(resolution.Issues.Select(i => new DifficultyDiagnostic(i.Code, i.Detail, row.Ordinal)));
                status = resolution.IsResolved ? DifficultyRowStatus.Resolved : DifficultyRowStatus.Unresolved;
            }
            // 元キー重複の有効候補も調べ、別名の第三行が同じchartへ到達する競合を見落とさない。
            if (status == DifficultyRowStatus.Conflict && row.Candidate is { } duplicate &&
                input.AcquisitionStatus == "COMPLETE" && input.ReadComplete &&
                diagnostics.All(d => d.Code == "DUPLICATE_SOURCE_KEY"))
            {
                resolution = ChartResolver.Resolve(new(duplicate.Title, duplicate.Difficulty, duplicate.PlayStyle,
                    duplicate.Tag, input.Table.Level, duplicate.Notes, input.Table.SourceName), connection, transaction);
                diagnostics.AddRange(resolution.Issues.Select(i => new DifficultyDiagnostic(i.Code, i.Detail, row.Ordinal)));
            }
            rows.Add(new(row, status, resolution, diagnostics.AsReadOnly()));
        }
        // ソース識別が異なってもalias/正規化で同じ譜面へ到達する。全関係行を競合にする。
        var collisions = rows.Where(r => r.Resolution?.ChartId.HasValue == true).GroupBy(r => r.Resolution!.ChartId!.Value)
            .Where(g => g.Count() > 1).SelectMany(g => g.Select(r => r.Source.Ordinal)).ToHashSet();
        for (int i = 0; i < rows.Count; i++)
            if (collisions.Contains(rows[i].Source.Ordinal))
                rows[i] = rows[i] with
                {
                    Status = DifficultyRowStatus.Conflict,
                    Diagnostics = Array.AsReadOnly(rows[i].Diagnostics.Append(new DifficultyDiagnostic(
                        "DUPLICATE_CHART", "複数の元行が同じchart_idへ照合されました。表全体を保留します。", rows[i].Source.Ordinal)).ToArray())
                };
        token.ThrowIfCancellationRequested();
        return new(input, rows);
    }
}

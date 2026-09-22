using System.Text.Json;
using IIDXProgressDashboard.Database;
using Microsoft.Data.Sqlite;
using static IIDXProgressDashboard.Difficulty.DifficultyUpdateStorage;

namespace IIDXProgressDashboard.Difficulty;

/// <summary>1表の計画・消失確認・バックアップ・原子的反映。通常DBの選択や初期化は呼出側の責務。</summary>
public sealed class DifficultyUpdateService
{
    private readonly DatabaseInitializer database;
    private readonly string backupDirectory;
    // SQLite障害・終了ログ障害を実SQLで再現する内部テスト境界。通常の呼出側には公開しない。
    private readonly Action<SqliteConnection, SqliteTransaction, string>? checkpoint;
    private static readonly DifficultyChangeCounts Zero = new(0, 0, 0);

    public DifficultyUpdateService(DatabaseInitializer database, string backupDirectory)
        : this(database, backupDirectory, null) { }

    internal DifficultyUpdateService(DatabaseInitializer database, string backupDirectory,
        Action<SqliteConnection, SqliteTransaction, string>? checkpoint)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        this.backupDirectory = Path.GetFullPath(backupDirectory);
        this.checkpoint = checkpoint;
    }

    public Task<DifficultyUpdatePlan> PrepareAsync(DifficultyParseResult input, Guid? batchId = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenValidated(database);
        using var transaction = connection.BeginTransaction(deferred: true);
        return Prepare(input, connection, transaction, batchId, cancellationToken);
    }, cancellationToken);

    public Task<DifficultyUpdatePlan> PrepareCurrentAsync(DifficultyTableKind kind, long expectedRunId,
        long expectedGenerationId, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenValidated(database);
        using var transaction = connection.BeginTransaction(deferred: true);
        var table = DifficultyTableDefinition.Get(kind);
        var input = DifficultyMatchingAudit.ReadCurrent(connection, transaction, table.Code, expectedRunId, expectedGenerationId);
        return Prepare(input, connection, transaction, null, cancellationToken);
    }, cancellationToken);

    private DifficultyUpdatePlan Prepare(DifficultyParseResult input, SqliteConnection connection,
        SqliteTransaction transaction, Guid? batchId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = DifficultyMatchingService.Resolve(input with { Table = DifficultyTableDefinition.Get(input.Table.Kind) }, connection, transaction, token);
        var previous = ReadState(connection, transaction, result.Input.Table.Code);
        var keys = result.Rows.Select(r => r.Source.SourceKey).ToHashSet(StringComparer.Ordinal);
        // 不正・不完全入力から消失を推測しない。前回未解決だった元行も消失比較に含む。
        var missing = result.CanPrepareUpdate && previous.Input is not null
            ? previous.Input.Rows.Where(r => !keys.Contains(r.SourceKey)).Select(r => new DifficultyMissingRow(
                r.SourceKey!, r.OriginalTitle, r.Candidate?.Difficulty, r.Candidate?.RankCode,
                previous.State!.SourceChartIds.TryGetValue(r.SourceKey!, out var chart) ? chart : null)).ToArray()
            : [];
        token.ThrowIfCancellationRequested();
        return new(database.DatabasePath, Revision(connection, transaction, result.Input.Table.Code), result,
            previous.State, previous.Run, missing, Counts(result, ReadEntries(connection, transaction, result.Input.Table.Code)), batchId);
    }

    /// <param name="confirmedPlanId">MissingRowsの具体的差分を確認した場合だけ、そのplan.PlanIdを指定する。</param>
    public Task<DifficultyUpdateResult> ApplyAsync(DifficultyUpdatePlan plan, Guid? confirmedPlanId = null,
        CancellationToken cancellationToken = default, IProgress<int>? progress = null) =>
        // 開始済み試行のキャンセルもFAILEDとして残すため、Task.Run自体にはtokenを渡さない。
        Task.Run(() => Apply(plan, confirmedPlanId, cancellationToken, progress));

    private DifficultyUpdateResult Apply(DifficultyUpdatePlan plan, Guid? confirmedPlanId, CancellationToken token, IProgress<int>? progress)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.DatabasePath, database.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("別DBの変更案は反映できません。");
        if (confirmedPlanId.HasValue && confirmedPlanId != plan.PlanId)
            throw new InvalidOperationException("消失確認は提示した変更案にのみ有効です。");
        using var connection = OpenValidated(database);
        string backup;
        long run;
        // 書込予約下で確定済みDBを保全する。失敗時はRUNNINGも作成しない。
        using (var preparation = connection.BeginTransaction(deferred: false))
        {
            backup = DatabaseBackup.Create(database, backupDirectory);
            run = Convert.ToInt64(Scalar(connection, preparation, """
                INSERT INTO import_runs(source_type,source_name,source_fingerprint,status,options_json,records_read,started_at)
                VALUES('DIFFICULTY_TABLE',$table,$hash,'RUNNING',$options,$read,$now) RETURNING import_run_id;
                """, ("$table", plan.TableCode), ("$hash", plan.Matching.Input.Source?.Sha256),
                ("$options", Options(plan, "HELD", null, Zero, 0, 0, 0, false)),
                ("$read", plan.Matching.Rows.Count), ("$now", Now())));
            preparation.Commit();
        }
        try
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            token.ThrowIfCancellationRequested();
            var previous = ReadState(connection, transaction, plan.TableCode, run);
            if (previous.Run != plan.PreviousRunId || Revision(connection, transaction, plan.TableCode) != plan.Revision)
                throw new InvalidOperationException("変更案の作成後に照合・表・所有状態が変化しました。再準備してください。");
            var result = DifficultyMatchingService.Resolve(plan.Matching.Input, connection, transaction, token);
            if (DifficultyMatchingAudit.CreateSnapshot(result) != plan.SnapshotJson)
                throw new InvalidOperationException("変更案と反映時の照合結果が異なります。再準備してください。");
            var counts = Zero;
            DifficultyUpdateState? state = null;
            int tables = 0, ranks = 0;
            string status, applyStatus;
            bool confirmed = confirmedPlanId == plan.PlanId;
            if (!result.CanPrepareUpdate) { status = "FAILED"; applyStatus = "FAILED"; }
            else if (plan.RequiresMissingConfirmation && !confirmed) { status = "PARTIAL"; applyStatus = "HELD"; }
            else
            {
                counts = Counts(result, ReadEntries(connection, transaction, plan.TableCode));
                (tables, ranks) = UpsertMetadata(connection, transaction, result, Now());
                checkpoint?.Invoke(connection, transaction, "BEFORE_ENTRIES");
                int processed = 0;
                foreach (var row in result.Rows.Where(r => r.ChartId.HasValue))
                {
                    token.ThrowIfCancellationRequested();
                    UpsertEntry(connection, transaction, plan.TableCode, row, run, Now());
                    // 通知は試行中の進捗。ここで報告した行数はcommit済み件数ではない。
                    progress?.Report(++processed);
                }
                state = CreateState(plan, result, run, TableFingerprint(connection, transaction, plan.TableCode));
                status = result.Count(DifficultyRowStatus.Unresolved) > 0 || state.RetainedChartIds.Length > 0 || state.MissingSourceKeys.Length > 0
                    ? "PARTIAL" : "SUCCESS";
                applyStatus = "APPLIED";
            }
            checkpoint?.Invoke(connection, transaction, "BEFORE_FINISH");
            Finish(connection, transaction, run, plan, result, status, applyStatus, state, counts, tables, ranks, confirmed, null);
            DifficultyMatchingAudit.SaveDiagnostics(connection, transaction, run, state?.AcceptedGenerationId, result, token);
            if (state is not null)
                DifficultyMatchingAudit.ResolvePendingAfterApply(connection, transaction, plan.TableCode, run, state.AcceptedGenerationId, token);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(run, backup, status, applyStatus, counts, Unresolved(result), plan.MissingRows.Count, state?.RetainedChartIds.Length ?? 0);
        }
        catch (Exception error)
        {
            // 反映transactionを破棄してから別transactionへ。診断も再保存し、反映数を必ず0にする。
            error.Data["DifficultyImportRunId"] = run;
            error.Data["DifficultyBackupPath"] = backup;
            try
            {
                using var failure = connection.BeginTransaction(deferred: false);
                checkpoint?.Invoke(connection, failure, "BEFORE_FAILURE_LOG");
                var result = plan.Matching;
                Finish(connection, failure, run, plan, result, "FAILED", "FAILED", null, Zero, 0, 0, false,
                    error is OperationCanceledException ? "CANCELLED: キャンセルされました。" : $"{error.GetType().Name}: {error.Message}");
                DifficultyMatchingAudit.SaveDiagnostics(connection, failure, run, null, result);
                failure.Commit();
            }
            catch (Exception loggingError)
            {
                throw new AggregateException("反映と失敗記録の両方に失敗しました。残存RUNNINGとバックアップを確認してください。", error, loggingError);
            }
            throw;
        }
    }

    private static DifficultyUpdateState CreateState(DifficultyUpdatePlan plan, DifficultyMatchingResult result, long run, string fingerprint)
    {
        var previous = plan.Previous;
        var source = result.Input.Source!;
        bool sameGeneration = previous?.InputSha256 == source.Sha256 && previous.InputKind == source.InputKind;
        var keys = result.Rows.Select(r => r.Source.SourceKey!).Order(StringComparer.Ordinal).ToArray();
        var mapping = previous is null ? new Dictionary<string, long>(StringComparer.Ordinal) : new(previous.SourceChartIds, StringComparer.Ordinal);
        foreach (var row in result.Rows.Where(r => r.ChartId.HasValue)) mapping[row.Source.SourceKey!] = row.ChartId!.Value;
        var resolved = result.Rows.Where(r => r.ChartId.HasValue).Select(r => r.ChartId!.Value).ToHashSet();
        var owned = (previous?.OwnedChartIds ?? []).Union(resolved).Order().ToArray();
        return new(1, plan.TableCode, sameGeneration ? previous!.AcceptedGenerationId : run,
            sameGeneration ? previous!.ParentGenerationId : previous?.AcceptedGenerationId,
            source.Sha256, source.InputKind, DifficultyParseResult.ContractVersion, keys, mapping, owned,
            (previous?.MissingSourceKeys ?? []).Union(plan.MissingRows.Select(r => r.SourceKey)).Except(keys).Order(StringComparer.Ordinal).ToArray(),
            owned.Except(resolved).Order().ToArray(), fingerprint);
    }

    private static string Options(DifficultyUpdatePlan plan, string applyStatus, DifficultyUpdateState? state,
        DifficultyChangeCounts counts, int retained, int tables, int ranks, bool confirmed)
    {
        using var snapshot = JsonDocument.Parse(plan.SnapshotJson);
        var result = plan.Matching;
        return JsonSerializer.Serialize(new
        {
            version = 1, plan.PlanId, plan.BatchId, baselineRunId = plan.PreviousRunId,
            acquisitionStatus = result.Input.AcquisitionStatus, parseStatus = result.Input.ParseStatus, result.Input.ReadComplete,
            applyStatus, stateCommitted = state is not null, difficultyState = state, snapshot = snapshot.RootElement,
            missingConfirmed = confirmed, missingRows = plan.MissingRows, plannedCounts = plan.PlannedCounts,
            counts = new { read = result.Rows.Count, resolved = result.Count(DifficultyRowStatus.Resolved),
                unresolved = result.Count(DifficultyRowStatus.Unresolved), invalid = result.Count(DifficultyRowStatus.Invalid),
                conflict = result.Count(DifficultyRowStatus.Conflict), notProcessed = result.Count(DifficultyRowStatus.NotProcessed),
                counts.Added, counts.Updated, counts.Unchanged, missing = plan.MissingRows.Count, retained,
                tablesChanged = tables, ranksChanged = ranks }
        }, DifficultyMatchingAudit.JsonOptions);
    }

    private static void Finish(SqliteConnection connection, SqliteTransaction transaction, long run, DifficultyUpdatePlan plan,
        DifficultyMatchingResult result, string status, string applyStatus, DifficultyUpdateState? state,
        DifficultyChangeCounts counts, int tables, int ranks, bool confirmed, string? message)
    {
        if (Execute(connection, transaction, """
            UPDATE import_runs SET status=$status,options_json=$options,records_imported=$imported,records_unresolved=$unresolved,
              completed_at=$now,message=$message WHERE import_run_id=$run AND status='RUNNING';
            """, ("$status", status), ("$options", Options(plan, applyStatus, state, counts, state?.RetainedChartIds.Length ?? 0, tables, ranks, confirmed)),
            ("$imported", counts.Added + counts.Updated), ("$unresolved", Unresolved(result)),
            ("$now", Now()), ("$message", message), ("$run", run)) != 1)
            throw new InvalidOperationException("実行ログの終了記録を確定できません。");
    }

    private static int Unresolved(DifficultyMatchingResult result) => result.Count(DifficultyRowStatus.Unresolved) +
        result.Count(DifficultyRowStatus.Invalid) + result.Count(DifficultyRowStatus.Conflict);
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
}

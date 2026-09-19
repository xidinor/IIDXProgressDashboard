using System.Globalization;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Import;

/// <summary>旧履歴を元行単位で保持する、再実行可能な取込サービス。実プレイの内容で間引かない。</summary>
public sealed class LegacyInfinitasLogImporter
{
    public const string SourceSystem = "LEGACY_INFINITAS_LOG";
    private readonly DatabaseInitializer database;
    private readonly string backupDirectory;

    public LegacyInfinitasLogImporter(DatabaseInitializer database, string backupDirectory)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        this.backupDirectory = Path.GetFullPath(backupDirectory);
    }

    /// <summary>sourceIdは呼出し側が一度発行・保存し、同じ元DBのコピー・改名・追記後も再利用する。</summary>
    /// <remarks>別の元DBには別IDを指定する。毎回の自動発行やパスからのID生成はしない。</remarks>
    public Task<LegacyImportResult> ImportAsync(string sourcePath, Guid sourceId,
        CancellationToken cancellationToken = default, IProgress<int>? progress = null) =>
        Task.Run(() => Import(sourcePath, sourceId, cancellationToken, progress));

    /// <summary>将来の自動移行用候補選択。優先候補が不正でも旧ログへ黙って切り替えない。</summary>
    public static string? SelectPreferredSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        foreach (var name in new[] { "iidx-progress.db", "infinitas_log.db" })
        {
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private LegacyImportResult Import(string sourcePath, Guid sourceId, CancellationToken token, IProgress<int>? progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (sourceId == Guid.Empty) throw new ArgumentException("保存・再利用する移行元IDを指定してください。", nameof(sourceId));
        var input = Path.GetFullPath(sourcePath);
        // 出力接続を開く前に、同名・リンク経由の原本書込を拒否する。
        LegacyFileIdentity.EnsureDifferent(input, database.DatabasePath);
        using var connection = database.OpenConnection();
        if ((long)Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';")! == 0)
            throw new InvalidOperationException("出力DBを先に初期化してください。");
        new MigrationRunner().Run(connection);

        long runId;
        string backup;
        using (var preparation = connection.BeginTransaction(deferred: false))
        {
            // バックアップ失敗ではRUNNINGも書かない。別接続で確定済みのSQLite snapshotを保存する。
            backup = DatabaseBackup.Create(database, backupDirectory);
            Execute(connection, preparation, """
                INSERT INTO import_runs(source_type,source_name,source_path,options_json,status)
                VALUES ($system,$name,$path,$options,'RUNNING');
                """, ("$system", SourceSystem), ("$name", sourceId.ToString("N")), ("$path", input),
                ("$options", Options(sourceId)));
            runId = (long)Scalar(connection, preparation, "SELECT last_insert_rowid();")!;
            preparation.Commit();
        }

        int read = 0;
        string? fingerprint = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var snapshot = LegacySourceReader.Read(input, token);
            read = snapshot.Rows.Count;
            fingerprint = snapshot.Fingerprint;
            int imported = 0, duplicates = 0, unresolved = 0, invalid = 0, conflicts = 0;
            using var transaction = connection.BeginTransaction(deferred: false);
            // 同時取込はこの書込ロックで直列化し、既存キー照会とINSERTを不可分にする。
            foreach (var row in snapshot.Rows)
            {
                token.ThrowIfCancellationRequested();
                var key = sourceId.ToString("N") + ":" + row.Id.ToString(CultureInfo.InvariantCulture);
                var previous = Scalar(connection, transaction, """
                    SELECT raw_data FROM play_history WHERE source_system=$system AND source_record_key=$key;
                    """, ("$system", SourceSystem), ("$key", key));
                if (previous is not null)
                {
                    if (Equals(previous, row.RawData)) duplicates++;
                    else
                    {
                        SaveUnresolved(connection, transaction, runId, key, row, "SOURCE_ROW_CHANGED", "登録済み元行の内容が変更されています。履歴は変更しません。");
                        conflicts++;
                    }
                }
                else
                {
                    // 最初に保持した元行を基準とする。未解決のまま入力が修正された場合も自動訂正しない。
                    var baseline = Scalar(connection, transaction, """
                        SELECT raw_data FROM unresolved_imports WHERE source_system=$system AND source_record_key=$key
                        ORDER BY unresolved_id LIMIT 1;
                        """, ("$system", SourceSystem), ("$key", key));
                    if (baseline is not null && !Equals(baseline, row.RawData))
                    {
                        SaveUnresolved(connection, transaction, runId, key, row, "SOURCE_ROW_CHANGED", "初回に保持した元行から内容が変更されています。訂正判断が必要です。");
                        conflicts++;
                    }
                    else
                    {
                        var converted = LegacyRowConverter.Convert(row.Values);
                        if (converted.Issues.Count > 0)
                        {
                            SaveUnresolved(connection, transaction, runId, key, row, converted.Issues[0].Code, JsonSerializer.Serialize(converted.Issues));
                            invalid++;
                        }
                        else
                        {
                            var resolution = ChartResolver.Resolve(converted.Request, connection, transaction);
                            if (!resolution.IsResolved)
                            {
                                SaveUnresolved(connection, transaction, runId, key, row, resolution.Issues[0].Code,
                                    JsonSerializer.Serialize(new { resolution.Issues, resolution.Candidates }));
                                if (resolution.Issues.Any(i => i.Code.StartsWith("INVALID_", StringComparison.Ordinal))) invalid++;
                                else unresolved++;
                            }
                            else
                            {
                                InsertPlay(connection, transaction, runId, key, row, converted, resolution.ChartId!.Value);
                                Execute(connection, transaction, """
                                    UPDATE unresolved_imports SET status='RESOLVED',resolved_chart_id=$chart,resolved_at=$now
                                    WHERE source_system=$system AND source_record_key=$key AND raw_data=$raw AND status='PENDING';
                                    """, ("$chart", resolution.ChartId.Value), ("$now", Now()), ("$system", SourceSystem), ("$key", key), ("$raw", row.RawData));
                                imported++;
                            }
                        }
                    }
                }
                progress?.Report(imported + duplicates + unresolved + invalid + conflicts);
            }
            var rejected = unresolved + invalid + conflicts;
            var status = rejected == 0 ? "SUCCESS" : "PARTIAL";
            Execute(connection, transaction, """
                UPDATE import_runs SET status=$status,records_read=$read,records_imported=$imported,
                    records_unresolved=$unresolved,source_fingerprint=$fingerprint,options_json=$options,completed_at=$now
                WHERE import_run_id=$id;
                """, ("$status", status), ("$read", read), ("$imported", imported), ("$unresolved", rejected),
                ("$fingerprint", fingerprint), ("$options", Options(sourceId, duplicates, unresolved, invalid, conflicts)), ("$now", Now()), ("$id", runId));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(runId, backup, status, read, imported, duplicates, unresolved, invalid, conflicts);
        }
        catch (Exception error)
        {
            // usingにより取込全体を戻した後に、失敗ログだけを別トランザクションで確定する。
            error.Data["LegacyImportRunId"] = runId;
            error.Data["LegacyBackupPath"] = backup;
            try
            {
                Execute(connection, null, """
                    UPDATE import_runs SET status='FAILED',records_read=$read,records_imported=0,records_unresolved=0,
                        source_fingerprint=$fingerprint,completed_at=$now,message=$message WHERE import_run_id=$id;
                    """, ("$read", read), ("$fingerprint", fingerprint), ("$now", Now()), ("$id", runId),
                    ("$message", error is OperationCanceledException ? "CANCELLED: 取込全体をロールバックしました。" : error.GetType().Name + ": " + error.Message));
            }
            catch (Exception loggingError)
            {
                throw new AggregateException("取込と失敗記録の両方が失敗しました。RUNNINGのログとバックアップを確認してください。", error, loggingError);
            }
            throw;
        }
    }

    private static void InsertPlay(SqliteConnection c, SqliteTransaction t, long runId, string key,
        LegacySourceRow row, LegacyConvertedRow value, long chart) => Execute(c, t, """
        INSERT INTO play_history(chart_id,played_at,clear_lamp,score,miss_count,level_at_play,total_notes_at_play,
            source_system,source_record_key,import_run_id,raw_data)
        VALUES ($chart,$date,$lamp,$score,$bp,$level,$notes,$system,$key,$run,$raw);
        """, ("$chart", chart), ("$date", value.PlayedAt), ("$lamp", value.Lamp), ("$score", value.Score),
        ("$bp", value.MissCount), ("$level", value.Request.Level), ("$notes", value.Request.TotalNotes),
        ("$system", SourceSystem), ("$key", key), ("$run", runId), ("$raw", row.RawData));

    private static void SaveUnresolved(SqliteConnection c, SqliteTransaction t, long runId, string key,
        LegacySourceRow row, string code, string detail) => Execute(c, t, """
        INSERT INTO unresolved_imports(import_run_id,source_system,source_record_key,entity_type,
            raw_song_name,raw_difficulty_type,reason_code,reason_detail,raw_data)
        VALUES ($run,$system,$key,'PLAY_HISTORY',$title,$difficulty,$code,$detail,$raw);
        """, ("$run", runId), ("$system", SourceSystem), ("$key", key),
        ("$title", row.Values["song_name"] as string), ("$difficulty", row.Values["difficulty_type"] as string),
        ("$code", code), ("$detail", detail), ("$raw", row.RawData));

    private static string Options(Guid id, int duplicates = 0, int unresolved = 0, int invalid = 0, int conflicts = 0) =>
        JsonSerializer.Serialize(new { contractVersion = 1, sourceId = id.ToString("N"), sourceTimeZone = "+09:00",
            playedAtPrecision = "minute", duplicates, unresolved, invalid, conflicts });

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] values)
    {
        var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] values)
    { using var command = Command(c, t, sql, values); return command.ExecuteScalar(); }

    private static void Execute(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] values)
    { using var command = Command(c, t, sql, values); command.ExecuteNonQuery(); }
}

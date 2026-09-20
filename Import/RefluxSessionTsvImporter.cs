using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Import;

public sealed record RefluxImportResult(long ImportRunId, string BackupPath, string Status,
    int Read, int Imported, int Duplicates, int Unresolved, int Invalid, int Conflicts, bool SessionHeld);

/// <summary>ファイル単位のSessionを、保存済みIDとデータ行番号で冪等に取り込む。</summary>
public sealed class RefluxSessionTsvImporter(DatabaseInitializer database, string backupDirectory)
{
    public const string SourceSystem = "REFLUX_SESSION_TSV";

    /// <summary>同じSessionのコピー・改名には同じID、別Sessionには別IDを呼出側で保存・再利用する。</summary>
    public Task<RefluxImportResult> ImportAsync(string sourcePath, Guid sessionId,
        RefluxImportOptions? options = null, CancellationToken cancellationToken = default,
        IProgress<int>? progress = null) => Task.Run(() => Import(sourcePath, sessionId,
            options ?? new(), cancellationToken, progress));

    private RefluxImportResult Import(string sourcePath, Guid id, RefluxImportOptions options,
        CancellationToken token, IProgress<int>? progress)
    {
        if (id == Guid.Empty) throw new ArgumentException("保存・再利用するSession IDが必要です。", nameof(id));
        options.GetTimeZone();
        var path = Path.GetFullPath(sourcePath);
        LegacyFileIdentity.EnsureDifferent(path, database.DatabasePath);
        using var connection = database.OpenConnection();
        if ((long)Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';")! == 0)
            throw new InvalidOperationException("出力DBを先に初期化してください。");
        new MigrationRunner().Run(connection);
        string backup;
        long run;
        using (var preparation = connection.BeginTransaction(deferred: false))
        {
            // 既存DBのsnapshotが保存できなければ、実行開始ログも書き込まない。
            backup = DatabaseBackup.Create(database, backupDirectory);
            Execute(connection, preparation, """
                INSERT INTO import_runs(source_type,source_name,source_path,options_json,status)
                VALUES ($system,$id,$path,$options,'RUNNING');
                """, ("$system", SourceSystem), ("$id", id.ToString("N")), ("$path", path),
                ("$options", SerializeOptions(id, options)));
            run = (long)Scalar(connection, preparation, "SELECT last_insert_rowid();")!;
            preparation.Commit();
        }
        int read = 0;
        string? fingerprint = null;
        try
        {
            var snapshot = RefluxSessionReader.Read(path, token);
            read = snapshot.Rows.Count;
            fingerprint = snapshot.Fingerprint;
            using var transaction = connection.BeginTransaction(deferred: false);
            // 登録・既存prefix検査・完了ログを同じ書込ロック内で行い、同時実行を直列化する。
            var baselineJson = Scalar(connection, transaction, """
                SELECT options_json FROM import_runs
                WHERE source_type=$system AND source_name=$id AND status IN ('SUCCESS','PARTIAL')
                    AND json_extract(options_json,'$.accepted')=1
                ORDER BY import_run_id DESC LIMIT 1;
                """, ("$system", SourceSystem), ("$id", id.ToString("N"))) as string;
            var hashes = RollingHashes(snapshot, token);
            var held = false;
            if (baselineJson is not null)
            {
                using var json = JsonDocument.Parse(baselineJson);
                var baseline = json.RootElement;
                var previousCount = baseline.GetProperty("rowCount").GetInt32();
                held = baseline.GetProperty("contractVersion").GetInt32() != 1 ||
                    baseline.GetProperty("timeMode").GetString() != options.TimeMode.ToString() ||
                    baseline.GetProperty("timeZoneId").GetString() != options.TimeZoneId ||
                    baseline.GetProperty("header").GetString() != snapshot.Header || previousCount > read ||
                    baseline.GetProperty("prefixHash").GetString() != hashes[Math.Min(previousCount, read)];
            }
            int imported = 0, duplicates = 0, unresolved = 0, invalid = 0, conflicts = 0;
            if (held)
            {
                // 削除で0行になった場合も、ファイル単位の競合証拠を保存する。既存履歴・基準は不変。
                SaveUnresolved(connection, transaction, run, null, null, "SESSION_CHANGED",
                    "既存部分・列・時刻設定の変更を検出しました。Session全体の追加を保留しました。",
                    JsonSerializer.Serialize(new { version = 1, snapshot.Header, rows = snapshot.Rows.Select(r => r.RawLine), options }));
                conflicts = read;
            }
            else
            {
                foreach (var row in snapshot.Rows)
                {
                    token.ThrowIfCancellationRequested();
                    var key = id.ToString("N") + ":" + (row.LineNumber - 1).ToString(CultureInfo.InvariantCulture);
                    var raw = JsonSerializer.Serialize(new { version = 1, sessionId = id.ToString("N"),
                        lineNumber = row.LineNumber, header = snapshot.Header, rawLine = row.RawLine, row = row.Values,
                        playedAtPrecision = "second", timeMode = options.TimeMode.ToString(), timeZoneId = options.TimeZoneId });
                    var previous = Scalar(connection, transaction,
                        "SELECT raw_data FROM play_history WHERE source_system=$system AND source_record_key=$key;",
                        ("$system", SourceSystem), ("$key", key));
                    if (previous is not null)
                    {
                        // 基準ログと矛盾する履歴がある場合も上書きせず、取込全体を失敗させる。
                        if (!Equals(previous, raw)) throw new InvalidDataException("保存済み元行とSession基準が矛盾しています。");
                        duplicates++;
                    }
                    else
                    {
                        var value = RefluxRowConverter.Convert(row, options);
                        if (value.Issues.Count > 0)
                        {
                            SaveUnresolved(connection, transaction, run, key, row, value.Issues[0].Code,
                                JsonSerializer.Serialize(value.Issues), raw);
                            invalid++;
                        }
                        else
                        {
                            var resolution = ChartResolver.Resolve(value.Request, connection, transaction);
                            if (!resolution.IsResolved)
                            {
                                SaveUnresolved(connection, transaction, run, key, row, resolution.Issues[0].Code,
                                    JsonSerializer.Serialize(new { resolution.Issues, resolution.Candidates }), raw);
                                unresolved++;
                            }
                            else
                            {
                                InsertPlay(connection, transaction, run, key, raw, value, resolution.ChartId!.Value);
                                Execute(connection, transaction, """
                                    UPDATE unresolved_imports SET status='RESOLVED',resolved_chart_id=$chart,resolved_at=$now
                                    WHERE source_system=$system AND source_record_key=$key AND raw_data=$raw AND status='PENDING';
                                    """, ("$chart", resolution.ChartId.Value), ("$now", Now()),
                                    ("$system", SourceSystem), ("$key", key), ("$raw", raw));
                                imported++;
                            }
                        }
                    }
                    progress?.Report(imported + duplicates + unresolved + invalid);
                }
            }
            var status = held || unresolved + invalid > 0 ? "PARTIAL" : "SUCCESS";
            Execute(connection, transaction, """
                UPDATE import_runs SET status=$status,records_read=$read,records_imported=$imported,
                    records_unresolved=$rejected,source_fingerprint=$fingerprint,options_json=$options,
                    completed_at=$now,message=$message WHERE import_run_id=$run;
                """, ("$status", status), ("$read", read), ("$imported", imported),
                ("$rejected", unresolved + invalid + conflicts), ("$fingerprint", fingerprint),
                ("$options", SerializeOptions(id, options, !held, snapshot.Header, read, hashes[^1],
                    duplicates, unresolved, invalid, conflicts, held)), ("$now", Now()), ("$run", run),
                ("$message", held ? "SESSION_CHANGED: Session全体を保留しました。" : null));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(run, backup, status, read, imported, duplicates, unresolved, invalid, conflicts, held);
        }
        catch (Exception error)
        {
            // 取込transactionをDisposeで戻してから、FAILEDだけを独立して確定する。
            error.Data["RefluxImportRunId"] = run;
            error.Data["RefluxBackupPath"] = backup;
            try
            {
                Execute(connection, null, """
                    UPDATE import_runs SET status='FAILED',records_read=$read,records_imported=0,records_unresolved=0,
                        source_fingerprint=$fingerprint,completed_at=$now,message=$message WHERE import_run_id=$run;
                    """, ("$read", read), ("$fingerprint", fingerprint), ("$now", Now()), ("$run", run),
                    ("$message", error is OperationCanceledException ? "CANCELLED: 全体をロールバックしました。" : error.GetType().Name + ": " + error.Message));
            }
            catch (Exception loggingError)
            {
                throw new AggregateException("取込と失敗ログ保存に失敗しました。RUNNINGとバックアップを確認してください。", error, loggingError);
            }
            throw;
        }
    }

    private static string[] RollingHashes(RefluxSessionSnapshot snapshot, CancellationToken token)
    {
        var hashes = new string[snapshot.Rows.Count + 1];
        hashes[0] = Hash(snapshot.Header);
        for (var i = 0; i < snapshot.Rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            hashes[i + 1] = Hash(hashes[i] + "\n" + snapshot.Rows[i].RawLine);
        }
        return hashes;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static string SerializeOptions(Guid id, RefluxImportOptions options, bool accepted = false,
        string? header = null, int rowCount = 0, string? prefixHash = null, int duplicates = 0,
        int unresolved = 0, int invalid = 0, int conflicts = 0, bool held = false) =>
        JsonSerializer.Serialize(new { contractVersion = 1, sessionId = id.ToString("N"),
            keyScheme = "session-guid:data-row-number", timeMode = options.TimeMode.ToString(),
            timeZoneId = options.TimeZoneId, playedAtPrecision = "second", accepted, header, rowCount,
            prefixHash, duplicates, unresolved, invalid, conflicts, held });

    private static void InsertPlay(SqliteConnection c, SqliteTransaction t, long run, string key,
        string raw, RefluxConvertedRow value, long chart)
    {
        using var command = Command(c, t, """
            INSERT INTO play_history(chart_id,played_at,clear_lamp,score,miss_count,gauge_percent,
                pgreat,great,good,bad,poor,combo_break,fast,slow,play_side,option_style_1,option_style_2,
                gauge_type,assist_type,range_type,level_at_play,total_notes_at_play,
                source_system,source_record_key,import_run_id,raw_data)
            VALUES ($chart,$date,$lamp,$exscore,$misscount,$gaugepercent,$pgreat,$great,$good,$bad,$poor,
                $combobreak,$fast,$slow,$playtype,$style,$style2,$gauge,$assist,$range,$level,$notecount,
                $system,$key,$run,$raw);
            """, ("$chart", chart), ("$date", value.PlayedAt), ("$lamp", value.Lamp),
            ("$system", SourceSystem), ("$key", key), ("$run", run), ("$raw", raw));
        foreach (var (name, number) in value.Numbers) command.Parameters.AddWithValue("$" + name, (object?)number ?? DBNull.Value);
        foreach (var (name, text) in value.Text) command.Parameters.AddWithValue("$" + name, (object?)text ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    private static void SaveUnresolved(SqliteConnection c, SqliteTransaction t, long run, string? key,
        RefluxSessionRow? row, string code, string detail, string raw) => Execute(c, t, """
        INSERT INTO unresolved_imports(import_run_id,source_system,source_record_key,entity_type,
            raw_song_name,raw_difficulty_type,reason_code,reason_detail,raw_data)
        VALUES ($run,$system,$key,$entity,$title,$difficulty,$code,$detail,$raw);
        """, ("$run", run), ("$system", SourceSystem), ("$key", key), ("$entity", row is null ? "SESSION" : "PLAY_HISTORY"),
        ("$title", row?.Values.GetValueOrDefault("title")), ("$difficulty", row?.Values.GetValueOrDefault("difficulty")),
        ("$code", code), ("$detail", detail), ("$raw", raw));

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

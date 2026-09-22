using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Master;

public sealed record ExternalSongUpdateResult(long ImportRunId, string BackupPath, string Status, int Read, int Imported, int Unresolved);

/// <summary>外部マスターだけを更新する。IDの付替え・欠落は全体保留、未知tagは元行を保持して保留する。</summary>
public sealed class ExternalSongUpdateService(DatabaseInitializer database, string backupDirectory)
{
    private const string SourceType = "EXTERNAL_SONG_MASTER";

    public Task<ExternalSongUpdateResult> UpdateAsync(Func<CancellationToken, Task<ExternalSongSnapshot>> acquire,
        CancellationToken token = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(acquire);
        using var connection = database.OpenConnection();
        if ((long)Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';")! == 0)
            throw new InvalidOperationException("出力DBを先に初期化してください。");
        new MigrationRunner().Run(connection);
        string backup;
        long run;
        using (var preparation = connection.BeginTransaction(deferred: false))
        {
            // バックアップを確保するまでは実行ログも変更しない。
            backup = DatabaseBackup.Create(database, backupDirectory);
            Execute(connection, preparation, """
                INSERT INTO import_runs(source_type,source_name,status) VALUES($type,$source,'RUNNING');
                """, ("$type", SourceType), ("$source", IidxDataTableProvider.SourceName));
            run = (long)Scalar(connection, preparation, "SELECT last_insert_rowid();")!;
            preparation.Commit();
        }
        ExternalSongSnapshot? snapshot = null;
        try
        {
            token.ThrowIfCancellationRequested();
            snapshot = await acquire(token).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            token.ThrowIfCancellationRequested();
            // 遅れて完了した古い取得を現在の対応へ上書きしない。
            var previous = Scalar(connection, transaction, """
                SELECT options_json FROM import_runs WHERE source_type=$type AND source_name=$source
                  AND status IN ('SUCCESS','PARTIAL')
                ORDER BY julianday(json_extract(options_json,'$.acquiredAt')) DESC,import_run_id DESC LIMIT 1;
                """, ("$type", SourceType), ("$source", IidxDataTableProvider.SourceName)) as string;
            if (previous is not null)
            {
                using var json = JsonDocument.Parse(previous);
                if (json.RootElement.GetProperty("contractVersion").GetInt32() != 1 ||
                    json.RootElement.GetProperty("parserVersion").GetInt32() != IidxDataTableProvider.ParserVersion ||
                    json.RootElement.GetProperty("normalizerVersion").GetString() != TitleNormalizer.Version)
                    throw new InvalidDataException("未対応の外部マスター状態です。");
                if (json.RootElement.GetProperty("acquiredAt").GetDateTimeOffset() > snapshot.AcquiredAt)
                    throw new InvalidDataException("STALE_EXTERNAL_MASTER: 古い取得結果は反映できません。");
            }
            var incoming = snapshot.Songs.ToDictionary(s => s.ExternalSongId);
            using (var query = Command(connection, transaction,
                "SELECT external_song_id,tag FROM external_song_ids WHERE source_name=$source;", ("$source", IidxDataTableProvider.SourceName)))
            using (var reader = query.ExecuteReader())
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    if (!incoming.TryGetValue(id, out var row))
                        throw new InvalidDataException($"EXTERNAL_ID_MISSING: ID={id} が配信から消失しました。既存対応を保持して全体を保留します。");
                    if (row.Tag != reader.GetString(1))
                        throw new InvalidDataException($"EXTERNAL_ID_REASSIGNED: ID={id} のtagが {reader.GetString(1)} から {row.Tag} へ変化しています。全体を保留します。");
                }
            var imported = 0;
            var unresolved = 0;
            foreach (var row in snapshot.Songs)
            {
                token.ThrowIfCancellationRequested();
                var raw = JsonSerializer.Serialize(new { version = 1, snapshot.Fingerprint, row });
                var key = row.ExternalSongId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (Scalar(connection, transaction, "SELECT 1 FROM songs WHERE tag=$tag;", ("$tag", row.Tag)) is null)
                {
                    // マスターにないtagはタイトルで別曲へ振り替えず、再取得時に再照合する。
                    Execute(connection, transaction, """
                        INSERT INTO unresolved_imports(import_run_id,source_system,source_record_key,entity_type,
                            raw_song_name,reason_code,reason_detail,raw_data)
                        VALUES($run,$source,$key,'EXTERNAL_SONG_ID',$title,'EXTERNAL_TAG_NOT_FOUND',$detail,$raw);
                        """, ("$run", run), ("$source", IidxDataTableProvider.SourceName), ("$key", key),
                        ("$title", row.Title), ("$detail", $"対応先tag={row.Tag}が既存songsにありません。"), ("$raw", raw));
                    unresolved++;
                    continue;
                }
                Execute(connection, transaction, """
                    INSERT INTO external_song_ids(source_name,external_song_id,title,normalized_title,tag)
                    VALUES($source,$id,$title,$normalized,$tag)
                    ON CONFLICT(source_name,external_song_id) DO UPDATE SET
                        title=excluded.title,normalized_title=excluded.normalized_title,is_active=1,updated_at=CURRENT_TIMESTAMP
                    WHERE external_song_ids.title<>excluded.title OR external_song_ids.normalized_title<>excluded.normalized_title
                        OR external_song_ids.is_active<>1;
                    """, ("$source", IidxDataTableProvider.SourceName), ("$id", row.ExternalSongId),
                    ("$title", row.Title), ("$normalized", row.NormalizedTitle), ("$tag", row.Tag));
                // 同じID・同じtagで保留した元行だけ解決済みにする。過去の別対応は履歴に残す。
                Execute(connection, transaction, """
                    UPDATE unresolved_imports SET status='RESOLVED',resolved_at=CURRENT_TIMESTAMP
                    WHERE source_system=$source AND entity_type='EXTERNAL_SONG_ID' AND source_record_key=$key
                      AND status='PENDING' AND json_extract(raw_data,'$.row.Tag')=$tag;
                    """, ("$source", IidxDataTableProvider.SourceName), ("$key", key), ("$tag", row.Tag));
                imported++;
            }
            var status = unresolved == 0 ? "SUCCESS" : "PARTIAL";
            Finish(connection, transaction, run, status, snapshot, imported, unresolved, null);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new ExternalSongUpdateResult(run, backup, status, snapshot.Songs.Count, imported, unresolved);
        }
        catch (Exception error)
        {
            // 登録transactionの破棄後に失敗ログだけ確定する。既存対応・履歴は元のまま。
            error.Data["ExternalSongImportRunId"] = run;
            error.Data["ExternalSongBackupPath"] = backup;
            try { Finish(connection, null, run, "FAILED", snapshot, 0, 0, error.GetType().Name + ": " + error.Message); }
            catch (Exception logError) { throw new AggregateException("更新と失敗記録の両方が失敗しました。", error, logError); }
            throw;
        }
    });

    private static void Finish(SqliteConnection connection, SqliteTransaction? transaction, long run, string status,
        ExternalSongSnapshot? snapshot, int imported, int unresolved, string? message) => Execute(connection, transaction, """
        UPDATE import_runs SET status=$status,completed_at=CURRENT_TIMESTAMP,records_read=$read,
            records_imported=$imported,records_unresolved=$unresolved,source_fingerprint=$hash,options_json=$options,message=$message
        WHERE import_run_id=$run;
        """, ("$run", run), ("$status", status), ("$read", snapshot?.Songs.Count ?? 0), ("$imported", imported),
        ("$unresolved", unresolved), ("$hash", snapshot?.Fingerprint), ("$message", message),
        ("$options", JsonSerializer.Serialize(new { contractVersion = 1, parserVersion = IidxDataTableProvider.ParserVersion,
            normalizerVersion = TitleNormalizer.Version, acquiredAt = snapshot?.AcquiredAt, sources = snapshot?.Sources })));

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static object? Scalar(SqliteConnection c, SqliteTransaction? t, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(c, t, sql, parameters); return command.ExecuteScalar(); }
    private static void Execute(SqliteConnection c, SqliteTransaction? t, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(c, t, sql, parameters); command.ExecuteNonQuery(); }
}

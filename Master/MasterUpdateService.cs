using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Master;

/// <summary>候補の取得・差分確認と、バックアップ付きの原子的DB反映を分離する。</summary>
public sealed class MasterUpdateService
{
    private const string SourceType = "MASTER";
    private const string SourceName = "TEXTAGE_ACTBL_CATALOG_V1";
    private readonly DatabaseInitializer database;
    private readonly string backupDirectory;
    // マスター・外部タイトル・aliasで同じ規則と版を使用する。
    public MasterUpdateService(DatabaseInitializer database, string backupDirectory)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        this.backupDirectory = Path.GetFullPath(backupDirectory);
    }

    public async Task<MasterUpdatePlan> PrepareAsync(Func<CancellationToken, Task<MasterSnapshot>> acquire,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        try
        {
            var snapshot = await acquire(cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => Prepare(snapshot, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 取得・解析段階の失敗も残す。ただし未知DBやバックアップ失敗ではDBに触れられない。
            await Task.Run(() => RecordPreparationFailure(error)).ConfigureAwait(false);
            throw;
        }
    }

    private MasterUpdatePlan Prepare(MasterSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        // 公開recordを手作り・with変更して一部候補を完全snapshotへ昇格させることを防ぐ。
        // 元入力を再解析し、モデルも一致する場合だけ採用する。
        var validated = new TextageMasterParser().Parse(snapshot.Sources, token);
        if (!validated.Songs.SequenceEqual(snapshot.Songs) || !validated.Charts.SequenceEqual(snapshot.Charts))
            throw new InvalidDataException("候補と元入力の解析結果が一致しません。");
        var songs = validated.Songs.ToArray();
        var charts = validated.Charts.ToArray();
        var titles = songs.Select(song => TitleNormalizer.Normalize(song.Title)).ToArray();
        if (titles.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("正規化曲名が空です。");
        using var connection = OpenValidated();
        using var transaction = connection.BeginTransaction(deferred: true);
        var previous = ReadState(connection, transaction);
        var existingSongs = ReadSongs(connection, transaction);
        var existingCharts = ReadCharts(connection, transaction);
        // 初回に既にある由来不明の行はUPSERTできるが、削除管理の所有権を獲得しない。
        var ownedSongs = (previous?.OwnedSongs ?? []).Union(songs.Select(s => s.Tag)
            .Where(tag => !existingSongs.ContainsKey(tag)), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var ownedCharts = (previous?.OwnedCharts ?? []).Union(charts.Select(Key)
            .Where(key => !existingCharts.ContainsKey(key))).OrderBy(k => k.Tag, StringComparer.Ordinal)
            .ThenBy(k => k.PlayStyle).ThenBy(k => k.Difficulty).ToArray();
        var presentSongs = songs.Select(s => s.Tag).ToHashSet(StringComparer.Ordinal);
        var presentCharts = charts.Select(Key).ToHashSet();
        var missingCharts = ownedCharts.Where(k => !presentCharts.Contains(k) && existingCharts.GetValueOrDefault(k)).ToArray();
        var missingChartSet = missingCharts.ToHashSet();
        // 範囲外の有効譜面が残る曲は無効にしない。参照は物理削除しない。
        var missingSongs = ownedSongs.Where(tag => !presentSongs.Contains(tag) && existingSongs.GetValueOrDefault(tag)
            && !existingCharts.Any(c => c.Key.Tag == tag && c.Value && !missingChartSet.Contains(c.Key))).ToArray();
        var fingerprint = Hash(JsonSerializer.Serialize(validated.Sources.Files.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new { p.Key, p.Value.Sha256, p.Value.ByteLength, p.Value.Encoding })));
        var state = new MasterUpdateState(1, validated.Scope, validated.ParserVersion, TitleNormalizer.Version,
            fingerprint, ownedSongs, ownedCharts);
        token.ThrowIfCancellationRequested();
        return new MasterUpdatePlan(database.DatabasePath, Revision(connection, transaction), songs, charts,
            titles, missingSongs, missingCharts, state);
    }

    /// <param name="confirmMissing">提示された欠落差分が意図した変更と確認できた場合だけtrueを指定する。</param>
    public Task<MasterUpdateResult> ApplyAsync(MasterUpdatePlan plan, bool confirmMissing = false,
        CancellationToken cancellationToken = default, IProgress<int>? progress = null) =>
        Task.Run(() => Apply(plan, confirmMissing, cancellationToken, progress));

    private MasterUpdateResult Apply(MasterUpdatePlan plan, bool confirmMissing, CancellationToken token, IProgress<int>? progress)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.DatabasePath, database.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("別DBで作成した変更案は反映できません。");
        using var connection = OpenValidated();
        string backup;
        long runId;
        // 予約書込ロック中に別の読取専用接続から確定済みDBをバックアップする。
        // バックアップ失敗時はRUNNINGの記録を含め何も変更しない。
        using (var preparation = connection.BeginTransaction(deferred: false))
        {
            backup = DatabaseBackup.Create(database, backupDirectory);
            runId = StartRun(connection, preparation, plan.State, plan.Songs.Count + plan.Charts.Count);
            preparation.Commit();
        }
        try
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            token.ThrowIfCancellationRequested();
            if (Revision(connection, transaction) != plan.Revision)
                throw new InvalidOperationException("変更案の作成後にマスターが更新されました。差分を再取得してください。");
            var now = DateTimeOffset.UtcNow.ToString("O");
            for (var i = 0; i < plan.Songs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var song = plan.Songs[i];
                Execute(connection, transaction, """
                    INSERT INTO songs(tag,title,normalized_title,artist,genre,version_name,sort_index,is_active,created_at,updated_at)
                    VALUES ($tag,$title,$normalized,$artist,$genre,$version,$sort,1,$now,$now)
                    ON CONFLICT(tag) DO UPDATE SET title=excluded.title,normalized_title=excluded.normalized_title,
                        artist=excluded.artist,genre=excluded.genre,version_name=excluded.version_name,
                        sort_index=excluded.sort_index,is_active=1,updated_at=excluded.updated_at;
                    """, ("$tag", song.Tag), ("$title", song.Title), ("$normalized", plan.NormalizedTitles[i]),
                    ("$artist", song.Artist), ("$genre", song.Genre), ("$version", song.VersionName), ("$sort", song.SortIndex), ("$now", now));
                progress?.Report(i + 1);
            }
            var processed = plan.Songs.Count;
            foreach (var chart in plan.Charts)
            {
                token.ThrowIfCancellationRequested();
                Execute(connection, transaction, """
                    INSERT INTO charts(tag,play_style,difficulty,level,total_notes,is_active,created_at,updated_at)
                    VALUES ($tag,$style,$difficulty,$level,$notes,1,$now,$now)
                    ON CONFLICT(tag,play_style,difficulty) DO UPDATE SET level=excluded.level,
                        total_notes=excluded.total_notes,is_active=1,updated_at=excluded.updated_at;
                    """, ("$tag", chart.Tag), ("$style", chart.PlayStyle), ("$difficulty", chart.Difficulty),
                    ("$level", chart.Level), ("$notes", chart.TotalNotes), ("$now", now));
                progress?.Report(++processed);
            }
            if (confirmMissing)
            {
                foreach (var key in plan.MissingCharts)
                {
                    token.ThrowIfCancellationRequested();
                    Execute(connection, transaction, "UPDATE charts SET is_active=0,updated_at=$now WHERE tag=$tag AND play_style=$style AND difficulty=$difficulty;",
                        ("$now", now), ("$tag", key.Tag), ("$style", key.PlayStyle), ("$difficulty", key.Difficulty));
                }
                foreach (var tag in plan.MissingSongs)
                {
                    token.ThrowIfCancellationRequested();
                    Execute(connection, transaction, "UPDATE songs SET is_active=0,updated_at=$now WHERE tag=$tag;", ("$now", now), ("$tag", tag));
                }
            }
            var songsRemoved = confirmMissing ? plan.MissingSongs.Count : 0;
            var chartsRemoved = confirmMissing ? plan.MissingCharts.Count : 0;
            var state = plan.State with { MissingConfirmed = confirmMissing, SongsDeactivated = songsRemoved, ChartsDeactivated = chartsRemoved };
            Execute(connection, transaction, """
                UPDATE import_runs SET status='SUCCESS',records_imported=$count,options_json=$options,completed_at=$now WHERE import_run_id=$id;
                """, ("$count", plan.Songs.Count + plan.Charts.Count), ("$options", JsonSerializer.Serialize(state)), ("$now", now), ("$id", runId));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(runId, backup, plan.Songs.Count, plan.Charts.Count, songsRemoved, chartsRemoved);
        }
        catch (Exception error)
        {
            // usingのtransaction破棄で全マスター変更を戻してから、独立して失敗結果を確定する。
            error.Data["MasterImportRunId"] = runId;
            error.Data["MasterBackupPath"] = backup;
            try { FinishFailure(connection, runId, error); }
            catch (Exception loggingError) { throw new AggregateException("反映と失敗記録の両方に失敗しました。RUNNINGとバックアップを確認してください。", error, loggingError); }
            throw;
        }
    }

    private void RecordPreparationFailure(Exception error)
    {
        try
        {
            using var connection = OpenValidated();
            using var transaction = connection.BeginTransaction(deferred: false);
            DatabaseBackup.Create(database, backupDirectory);
            var id = StartRun(connection, transaction, null, 0);
            Execute(connection, transaction, "UPDATE import_runs SET status='FAILED',completed_at=$now,message=$message WHERE import_run_id=$id;",
                ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$message", FailureMessage(error)), ("$id", id));
            transaction.Commit();
        }
        catch (Exception loggingError)
        {
            throw new AggregateException("取得・検証に失敗し、DBへの失敗記録も保存できませんでした。", error, loggingError);
        }
    }

    private SqliteConnection OpenValidated()
    {
        var connection = database.OpenConnection();
        try
        {
            // 空DBへの暗黙初期化を避ける。初期化は呼出し側の別工程。
            if (Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';") is not long count || count == 0)
                throw new InvalidOperationException("先に出力用DBを初期化してください。");
            new MigrationRunner().Run(connection);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static MasterUpdateState? ReadState(SqliteConnection connection, SqliteTransaction transaction)
    {
        var json = Scalar(connection, transaction, "SELECT options_json FROM import_runs WHERE source_type=$type AND source_name=$name AND status='SUCCESS' ORDER BY import_run_id DESC LIMIT 1;",
            ("$type", SourceType), ("$name", SourceName));
        if (json is null) return null;
        var state = json is string value ? JsonSerializer.Deserialize<MasterUpdateState>(value) : null;
        if (state is null || state.FormatVersion != 1 || state.Scope != SourceName || state.OwnedSongs is null || state.OwnedCharts is null)
            throw new InvalidDataException("前回マスター更新の所有範囲を解釈できません。");
        if (state.ParserVersion != "1") throw new InvalidDataException("前回とParser規則版が異なるため、所有範囲を引き継げません。");
        return state;
    }

    private static Dictionary<string, bool> ReadSongs(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT tag,is_active FROM songs;");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetBoolean(1));
        return result;
    }

    private static Dictionary<MasterChartKey, bool> ReadCharts(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT tag,play_style,difficulty,is_active FROM charts;");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<MasterChartKey, bool>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)), reader.GetBoolean(3));
        return result;
    }

    private static string Revision(SqliteConnection connection, SqliteTransaction transaction)
    {
        // 確認後の別更新を検出する。自分のRUNNING・FAILEDログは確認を無効にしない。
        var rows = new List<object[]>();
        foreach (var sql in new[] { "SELECT * FROM songs ORDER BY tag;", "SELECT * FROM charts ORDER BY chart_id;",
            "SELECT import_run_id,options_json FROM import_runs WHERE source_type='MASTER' AND source_name='TEXTAGE_ACTBL_CATALOG_V1' AND status='SUCCESS' ORDER BY import_run_id;" })
        {
            using var command = Command(connection, transaction, sql);
            using var reader = command.ExecuteReader();
            while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
        }
        return Hash(JsonSerializer.Serialize(rows));
    }

    private static long StartRun(SqliteConnection connection, SqliteTransaction transaction, MasterUpdateState? state, int count) =>
        Convert.ToInt64(Scalar(connection, transaction, """
            INSERT INTO import_runs(source_type,source_name,source_fingerprint,options_json,status,records_read,started_at)
            VALUES ($type,$name,$fingerprint,$options,'RUNNING',$count,$now) RETURNING import_run_id;
            """, ("$type", SourceType), ("$name", SourceName), ("$fingerprint", state?.Fingerprint),
            ("$options", state is null ? null : JsonSerializer.Serialize(state)), ("$count", count), ("$now", DateTimeOffset.UtcNow.ToString("O"))));

    private static void FinishFailure(SqliteConnection connection, long id, Exception error) =>
        Execute(connection, null, "UPDATE import_runs SET status='FAILED',records_imported=0,completed_at=$now,message=$message WHERE import_run_id=$id;",
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$message", FailureMessage(error)), ("$id", id));

    private static string FailureMessage(Exception error) => error is OperationCanceledException ? "CANCELLED: キャンセルされました。" : $"{error.GetType().Name}: {error.Message}";
    private static MasterChartKey Key(MasterChart chart) => new(chart.Tag, chart.PlayStyle, chart.Difficulty);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }
    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }
}

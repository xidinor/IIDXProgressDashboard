using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Difficulty;

/// <summary>難易度表更新の所有検査とSQL。全操作のtransactionはサービス側で管理する。</summary>
internal static class DifficultyUpdateStorage
{
    internal static SqliteConnection OpenValidated(DatabaseInitializer database)
    {
        var connection = database.OpenConnection();
        try
        {
            if (Convert.ToInt64(Scalar(connection, null, "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';")) == 0)
                throw new InvalidOperationException("先に出力用DBを初期化してください。");
            new MigrationRunner().Run(connection);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static (long? Run, DifficultyUpdateState? State, DifficultyParseResult? Input) ReadState(
        SqliteConnection connection, SqliteTransaction transaction, string table, long? ownRun = null)
    {
        using var query = Command(connection, transaction,
            "SELECT import_run_id,status,options_json FROM import_runs WHERE source_type='DIFFICULTY_TABLE' AND source_name=$table ORDER BY import_run_id;", ("$table", table));
        long? latestRun = null;
        DifficultyUpdateState? state = null;
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                long run = reader.GetInt64(0);
                if (run == ownRun) continue;
                if (reader.GetString(1) == "RUNNING")
                    throw new InvalidOperationException($"未確定の難易度表run {run} があります。実行中か確認して復旧してください。");
                if (reader.IsDBNull(2)) throw new InvalidDataException("難易度表の監査JSONがありません。");
                using var doc = JsonDocument.Parse(reader.GetString(2));
                var root = doc.RootElement;
                if (root.GetProperty("version").GetInt32() != 1)
                    throw new InvalidDataException("未対応の難易度表監査形式です。");
                if (!root.GetProperty("stateCommitted").GetBoolean()) continue;
                var next = root.GetProperty("difficultyState").Deserialize<DifficultyUpdateState>(DifficultyMatchingAudit.JsonOptions)
                    ?? throw new InvalidDataException("所有状態がありません。");
                ValidateOwnershipTransition(state, next, root.GetProperty("snapshot"));
                state = next;
                latestRun = run;
            }
        }
        var tableId = Scalar(connection, transaction, "SELECT table_id FROM difficulty_tables WHERE table_code=$table;", ("$table", table));
        if (state is null)
        {
            if (tableId is not null) throw new InvalidDataException("未所有の既存表は名前一致で引き継げません。");
            return (null, null, null);
        }
        if (tableId is null || state.Version != 1 || state.RankDictionaryVersion != DifficultyParseResult.ContractVersion ||
            state.TableCode != table || state.SourceKeys is null || state.SourceChartIds is null || state.OwnedChartIds is null ||
            state.MissingSourceKeys is null || state.RetainedChartIds is null)
            throw new InvalidDataException("未知または不完全な難易度表所有状態です。");
        // 5-4の検査で全受理runの世代系列・snapshot版・原本との整合性も確認する。
        var input = DifficultyMatchingAudit.ReadCurrent(connection, transaction, table, latestRun!.Value, state.AcceptedGenerationId);
        var parsed = new DifficultyTableParser().Parse(input.Table.Kind, input.Source!);
        if (!parsed.IsValid || !parsed.ReadComplete || state.InputKind != input.Source!.InputKind ||
            !SameSet(state.SourceKeys, parsed.Rows.Select(r => r.SourceKey!)) ||
            state.OwnedChartIds.Any(id => id <= 0) || state.SourceChartIds.Any(p => p.Value <= 0 || !state.OwnedChartIds.Contains(p.Value)) ||
            state.RetainedChartIds.Except(state.OwnedChartIds).Any() || state.MissingSourceKeys.Intersect(state.SourceKeys).Any() ||
            state.TableFingerprint != TableFingerprint(connection, transaction, table))
            throw new InvalidDataException("所有状態・原本・既存表が一致しません。自動接収せず調査してください。");
        var entries = ReadEntries(connection, transaction, table);
        if (!SameSet(state.OwnedChartIds, entries.Keys)) throw new InvalidDataException("所有外または失われた表エントリーがあります。");
        var definition = DifficultyTableDefinition.Get(input.Table.Kind);
        if (Convert.ToInt64(Scalar(connection, transaction, """
            SELECT count(*) FROM difficulty_tables WHERE table_code=$table AND level=$level
              AND play_style='SP' AND gauge_type=$gauge AND source_name=$source;
            """, ("$table", table), ("$level", definition.Level), ("$gauge", definition.Gauge), ("$source", definition.SourceName))) != 1)
            throw new InvalidDataException("既存表の所有範囲が異なります。");
        return (latestRun, state, input);
    }

    private static bool SameSet<T>(IEnumerable<T> left, IEnumerable<T> right) where T : notnull
    {
        var array = left.ToArray();
        return array.Length == array.Distinct().Count() && array.ToHashSet().SetEquals(right);
    }

    private static void ValidateOwnershipTransition(DifficultyUpdateState? previous, DifficultyUpdateState state, JsonElement snapshot)
    {
        if (state.Version != 1 || state.RankDictionaryVersion != DifficultyParseResult.ContractVersion ||
            state.SourceKeys is null || state.SourceChartIds is null || state.OwnedChartIds is null ||
            state.MissingSourceKeys is null || state.RetainedChartIds is null)
            throw new InvalidDataException("未対応または欠落した所有状態です。");
        var keys = new List<string>();
        var resolved = new HashSet<long>();
        var mapping = previous is null ? new Dictionary<string, long>(StringComparer.Ordinal) : new(previous.SourceChartIds, StringComparer.Ordinal);
        foreach (var row in snapshot.GetProperty("rows").EnumerateArray())
        {
            string key = row.GetProperty("source").GetProperty("sourceKey").GetString()
                ?? throw new InvalidDataException("受理済み元行の識別がありません。");
            keys.Add(key);
            var status = (DifficultyRowStatus)row.GetProperty("status").GetInt32();
            if (status == DifficultyRowStatus.Resolved)
            {
                long chart = row.GetProperty("chartId").GetInt64();
                if (chart <= 0 || !resolved.Add(chart)) throw new InvalidDataException("受理済み譜面の識別が競合しています。");
                mapping[key] = chart;
            }
            else if (status != DifficultyRowStatus.Unresolved)
                throw new InvalidDataException("不正・競合行を受理状態に含めることはできません。");
        }
        var owned = (previous?.OwnedChartIds ?? []).Union(resolved).ToHashSet();
        var missing = (previous?.MissingSourceKeys ?? []).Union((previous?.SourceKeys ?? []).Except(keys)).Except(keys);
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count || !SameSet(state.SourceKeys, keys) ||
            !SameSet(state.OwnedChartIds, owned) || !SameSet(state.RetainedChartIds, owned.Except(resolved)) ||
            !SameSet(state.MissingSourceKeys, missing) || state.SourceChartIds.Count != mapping.Count ||
            mapping.Any(p => !state.SourceChartIds.TryGetValue(p.Key, out var chart) || chart != p.Value))
            throw new InvalidDataException("snapshotと所有・消失・保持集合の世代間の遷移が一致しません。");
    }

    internal sealed record Entry(long ChartId, string RankCode, string? Title, string? Difficulty);
    internal static Dictionary<long, Entry> ReadEntries(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = Command(connection, transaction, """
            SELECT e.chart_id,e.rank_code,e.source_title,e.source_difficulty FROM difficulty_table_entries e
            JOIN difficulty_tables t USING(table_id) WHERE t.table_code=$table ORDER BY e.chart_id;
            """, ("$table", table));
        using var reader = command.ExecuteReader();
        var entries = new Dictionary<long, Entry>();
        while (reader.Read()) entries.Add(reader.GetInt64(0), new(reader.GetInt64(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return entries;
    }

    internal static bool SameEntry(Entry entry, DifficultyMatchedRow row) =>
        entry.RankCode == row.Source.Candidate!.RankCode && entry.Title == row.Source.Candidate.Title && entry.Difficulty == row.Source.Candidate.Difficulty;

    internal static DifficultyChangeCounts Counts(DifficultyMatchingResult result, Dictionary<long, Entry> existing)
    {
        int added = 0, updated = 0, unchanged = 0;
        foreach (var row in result.Rows.Where(r => r.ChartId.HasValue))
            if (!existing.TryGetValue(row.ChartId!.Value, out var entry)) added++;
            else if (SameEntry(entry, row)) unchanged++;
            else updated++;
        return new(added, updated, unchanged);
    }

    internal static string TableFingerprint(SqliteConnection connection, SqliteTransaction transaction, string table) => Fingerprint(connection, transaction, table,
        ["SELECT * FROM difficulty_tables WHERE table_code=$table;",
         "SELECT r.* FROM difficulty_ranks r JOIN difficulty_tables t USING(table_id) WHERE t.table_code=$table ORDER BY r.rank_code;",
         "SELECT e.* FROM difficulty_table_entries e JOIN difficulty_tables t USING(table_id) WHERE t.table_code=$table ORDER BY e.chart_id;"]);

    internal static string Revision(SqliteConnection connection, SqliteTransaction transaction, string table) => Fingerprint(connection, transaction, table,
        ["SELECT * FROM songs ORDER BY tag;", "SELECT * FROM charts ORDER BY chart_id;", "SELECT * FROM song_aliases ORDER BY alias_id;",
         "SELECT * FROM difficulty_tables WHERE table_code=$table;",
         "SELECT r.* FROM difficulty_ranks r JOIN difficulty_tables t USING(table_id) WHERE t.table_code=$table ORDER BY r.rank_code;",
         "SELECT e.* FROM difficulty_table_entries e JOIN difficulty_tables t USING(table_id) WHERE t.table_code=$table ORDER BY e.chart_id;",
         "SELECT import_run_id,status,options_json FROM import_runs WHERE source_type='DIFFICULTY_TABLE' AND source_name=$table AND status IN ('SUCCESS','PARTIAL') AND json_extract(options_json,'$.stateCommitted')=1 ORDER BY import_run_id;"]);

    private static string Fingerprint(SqliteConnection connection, SqliteTransaction transaction, string table, string[] queries)
    {
        // テーブル境界・NULLも含めて直列化。自身のRUNNING/FAILEDは前提変更としない。
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var sql in queries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(sql));
            using var command = Command(connection, transaction, sql, ("$table", table));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray();
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static (int Tables, int Ranks) UpsertMetadata(SqliteConnection connection, SqliteTransaction transaction,
        DifficultyMatchingResult result, string now)
    {
        var table = result.Input.Table;
        int tables = Execute(connection, transaction, """
            INSERT INTO difficulty_tables(table_code,display_name,level,play_style,gauge_type,source_name,source_url,source_revision,updated_at)
            VALUES($table,$name,$level,'SP',$gauge,$source,$url,$revision,$now)
            ON CONFLICT(table_code) DO UPDATE SET display_name=excluded.display_name,source_url=excluded.source_url,
                source_revision=excluded.source_revision,updated_at=excluded.updated_at
            WHERE display_name IS NOT excluded.display_name OR source_url IS NOT excluded.source_url OR source_revision IS NOT excluded.source_revision;
            """, ("$table", table.Code), ("$name", table.DisplayName), ("$level", table.Level), ("$gauge", table.Gauge),
            ("$source", table.SourceName), ("$url", table.Url), ("$revision", result.Input.Source?.SourceRevision), ("$now", now));
        var tableId = Scalar(connection, transaction, "SELECT table_id FROM difficulty_tables WHERE table_code=$table;", ("$table", table.Code));
        int ranks = 0;
        foreach (var rank in table.Ranks)
            ranks += Execute(connection, transaction, """
                INSERT INTO difficulty_ranks(table_id,rank_code,display_name,rank_kind,sort_order) VALUES($id,$code,$name,$kind,$sort)
                ON CONFLICT(table_id,rank_code) DO UPDATE SET display_name=excluded.display_name,rank_kind=excluded.rank_kind,sort_order=excluded.sort_order
                WHERE display_name IS NOT excluded.display_name OR rank_kind IS NOT excluded.rank_kind OR sort_order IS NOT excluded.sort_order;
                """, ("$id", tableId), ("$code", rank.Code), ("$name", rank.DisplayName), ("$kind", rank.Kind), ("$sort", rank.SortOrder));
        return (tables, ranks);
    }

    internal static void UpsertEntry(SqliteConnection connection, SqliteTransaction transaction, string table,
        DifficultyMatchedRow row, long run, string now) => Execute(connection, transaction, """
            INSERT INTO difficulty_table_entries(table_id,chart_id,rank_code,source_title,source_difficulty,import_run_id,updated_at)
            VALUES((SELECT table_id FROM difficulty_tables WHERE table_code=$table),$chart,$rank,$title,$difficulty,$run,$now)
            ON CONFLICT(table_id,chart_id) DO UPDATE SET rank_code=excluded.rank_code,source_title=excluded.source_title,
                source_difficulty=excluded.source_difficulty,import_run_id=excluded.import_run_id,updated_at=excluded.updated_at
            WHERE rank_code IS NOT excluded.rank_code OR source_title IS NOT excluded.source_title OR source_difficulty IS NOT excluded.source_difficulty;
            """, ("$table", table), ("$chart", row.ChartId), ("$rank", row.Source.Candidate!.RankCode),
            ("$title", row.Source.Candidate.Title), ("$difficulty", row.Source.Candidate.Difficulty), ("$run", run), ("$now", now));

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); return command.ExecuteScalar(); }
    internal static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); return command.ExecuteNonQuery(); }
}

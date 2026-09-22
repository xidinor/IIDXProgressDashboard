using System.Text.Json;
using System.Text.Json.Nodes;
using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Difficulty;

/// <summary>
/// 5-5の更新サービスから同じトランザクションで使う監査部品。
/// commit・バックアップ・RUNNING管理・表のUPSERTは呼出側の責務。
/// </summary>
internal static class DifficultyMatchingAudit
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static string CreateSnapshot(DifficultyMatchingResult result) => JsonSerializer.Serialize(new
    {
        version = 1, parserVersion = DifficultyParseResult.ParserVersion,
        contractVersion = DifficultyParseResult.ContractVersion, normalizerVersion = TitleNormalizer.Version,
        input = result.Input, rows = result.Rows
    }, JsonOptions);

    internal static void SaveDiagnostics(SqliteConnection connection, SqliteTransaction transaction,
        long runId, long? acceptedGenerationId, DifficultyMatchingResult result, CancellationToken token = default)
    {
        RequireRun(connection, transaction, runId, result);
        // 正常行もsnapshotに残す。これはoptions_json.snapshotへ格納する呼出側への前提条件。
        foreach (var row in result.Rows.Where(r => r.Status is DifficultyRowStatus.Unresolved or DifficultyRowStatus.Invalid or DifficultyRowStatus.Conflict))
        {
            token.ThrowIfCancellationRequested();
            var reason = row.Diagnostics.FirstOrDefault(d => row.Status != DifficultyRowStatus.Conflict ||
                d.Code is "DUPLICATE_SOURCE_KEY" or "DUPLICATE_CHART") ?? new("INVALID_ROW", "有効な候補がありません。", row.Source.Ordinal);
            Insert(connection, transaction, runId, "DIFFICULTY_TABLE_ENTRY", row.Source.RecordKey,
                row.Source.OriginalTitle, row.Source.Candidate?.Difficulty, result.Input.Table.Level,
                row.Source.Candidate?.Notes, reason.Code, JsonSerializer.Serialize(new { row.Status, row.Diagnostics, row.Resolution }, JsonOptions),
                JsonSerializer.Serialize(new
                {
                    version = 1, acceptedGenerationId, tableCode = result.Input.Table.Code,
                    inputSha256 = result.Input.Source?.Sha256, contractVersion = DifficultyParseResult.ContractVersion,
                    parserVersion = DifficultyParseResult.ParserVersion, source = Evidence(result.Input.Source),
                    row = row.Source, resolution = row.Resolution
                }, JsonOptions));
        }
        // ページ障害は曲行と別entityにし、records_unresolvedへ加算しない。
        foreach (var diagnostic in result.Input.Diagnostics.Where(d => d.RowOrdinal is null))
        {
            token.ThrowIfCancellationRequested();
            Insert(connection, transaction, runId, "DIFFICULTY_TABLE_SOURCE", null, null, null, null, null,
                diagnostic.Code, diagnostic.Detail, JsonSerializer.Serialize(new
                {
                    version = 1, acceptedGenerationId, tableCode = result.Input.Table.Code,
                    source = result.Input.Source, diagnostic
                }, JsonOptions));
        }
        token.ThrowIfCancellationRequested();
    }

    // 本文全体はrun snapshotに一度だけ保存する。各行へ8 MiBの本文を複製しない。
    private static object? Evidence(DifficultySource? source) => source is null ? null : new
    {
        source.RequestedUrl, source.FinalUrl, source.InputKind, source.Sha256, source.ByteLength,
        source.StartedAt, source.CompletedAt, source.HttpStatus, source.ContentType,
        source.ETag, source.LastModified, source.SourceRevision
    };

    // 再処理は元行単独ではなく、DB内の現在受理snapshot全体から行う。
    // expectedRunIdで同じ入力の再照合を挟んだ場合も古い計画を拒否する。
    internal static DifficultyMatchingResult ReprocessCurrent(SqliteConnection connection, SqliteTransaction transaction,
        string tableCode, long expectedRunId, long expectedGenerationId, CancellationToken token = default)
    {
        var input = ReadCurrent(connection, transaction, tableCode, expectedRunId, expectedGenerationId);
        return DifficultyMatchingService.Resolve(input, connection, transaction, token);
    }

    internal static DifficultyParseResult ReadCurrent(SqliteConnection connection, SqliteTransaction transaction,
        string tableCode, long expectedRunId, long expectedGenerationId)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT import_run_id,status,options_json FROM import_runs
            WHERE source_type='DIFFICULTY_TABLE' AND source_name=$table ORDER BY import_run_id;
            """;
        query.Parameters.AddWithValue("$table", tableCode);
        using var reader = query.ExecuteReader();
        long? generation = null, latestRun = null, generationParent = null;
        DifficultyParseResult? latest = null;
        while (reader.Read())
        {
            if (reader.IsDBNull(2)) throw new InvalidDataException("難易度表の監査JSONがありません。");
            using var doc = JsonDocument.Parse(reader.GetString(2));
            var root = doc.RootElement;
            if (!root.TryGetProperty("stateCommitted", out var committed) || !committed.GetBoolean()) continue;
            if (reader.GetString(1) is not ("SUCCESS" or "PARTIAL") || root.GetProperty("applyStatus").GetString() != "APPLIED")
                throw new InvalidDataException("受理状態と実行結果が矛盾しています。");
            var state = root.GetProperty("difficultyState");
            if (state.GetProperty("version").GetInt32() != 1 || state.GetProperty("tableCode").GetString() != tableCode)
                throw new InvalidDataException("未知の難易度表状態です。");
            long next = state.GetProperty("acceptedGenerationId").GetInt64();
            long? parent = state.GetProperty("parentGenerationId").ValueKind == JsonValueKind.Null
                ? null : state.GetProperty("parentGenerationId").GetInt64();
            long run = reader.GetInt64(0);
            if (next <= 0 || (next != generation && (next != run || parent != generation)) ||
                (next == generation && parent != generationParent))
                throw new InvalidDataException("難易度表の世代系列が不正です。");
            var snapshot = root.GetProperty("snapshot");
            if (snapshot.GetProperty("version").GetInt32() != 1 ||
                snapshot.GetProperty("parserVersion").GetInt32() != DifficultyParseResult.ParserVersion ||
                snapshot.GetProperty("contractVersion").GetInt32() != DifficultyParseResult.ContractVersion ||
                snapshot.GetProperty("normalizerVersion").GetString() != TitleNormalizer.Version)
                throw new InvalidDataException("未対応の照合snapshot版です。");
            var input = snapshot.GetProperty("input").Deserialize<DifficultyParseResult>(JsonOptions)
                ?? throw new InvalidDataException("入力snapshotがありません。");
            if (input.Source is null || input.Table.Code != tableCode ||
                DifficultyTableDefinition.Get(input.Table.Kind).Code != tableCode ||
                input.Source.Sha256 != state.GetProperty("inputSha256").GetString())
                throw new InvalidDataException("受理状態と原本が一致しません。");
            // 同じ世代の原本差し替えも拒否。A→B→Aではrun由来の別世代が必要。
            if (next == generation && (latest!.Source!.Sha256 != input.Source.Sha256 || latest.Source.InputKind != input.Source.InputKind))
                throw new InvalidDataException("同じ世代の元入力が変化しています。");
            if (next != generation && latest?.Source?.Sha256 == input.Source.Sha256 && latest.Source.InputKind == input.Source.InputKind)
                throw new InvalidDataException("連続した同一入力に新しい世代を発行できません。");
            generation = next;
            generationParent = parent;
            latestRun = run;
            latest = input;
        }
        if (latest is null || latestRun != expectedRunId || generation != expectedGenerationId)
            throw new InvalidOperationException("現在の受理世代・基準runと一致しません。再準備してください。");
        return latest;
    }

    internal static void ResolvePendingAfterApply(SqliteConnection connection, SqliteTransaction transaction,
        string tableCode, long currentRunId, long generationId, CancellationToken token = default)
    {
        // APPLIED状態・エントリー・PENDINGを同一transactionで確定する。HELD/失敗で呼ぶと拒否する。
        var result = ReprocessCurrent(connection, transaction, tableCode, currentRunId, generationId, token);
        if (!result.CanPrepareUpdate) throw new InvalidOperationException("競合・不正入力のPENDINGは解決できません。");
        foreach (var row in result.Rows.Where(r => r.ChartId.HasValue))
        {
            token.ThrowIfCancellationRequested();
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = """
                UPDATE unresolved_imports SET status='RESOLVED',resolved_chart_id=$chart,resolved_at=$now
                WHERE source_system='DIFFICULTY_TABLE' AND entity_type='DIFFICULTY_TABLE_ENTRY'
                  AND source_record_key=$key AND status='PENDING'
                  AND json_extract(raw_data,'$.tableCode')=$table
                  AND json_extract(raw_data,'$.acceptedGenerationId')=$generation
                  AND EXISTS (SELECT 1 FROM difficulty_table_entries e JOIN difficulty_tables t USING(table_id)
                    WHERE t.table_code=$table AND e.chart_id=$chart AND e.rank_code=$rank
                      AND e.source_title=$title AND e.source_difficulty=$difficulty);
                """;
            query.Parameters.AddWithValue("$chart", row.ChartId!.Value);
            query.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            query.Parameters.AddWithValue("$key", row.Source.RecordKey);
            query.Parameters.AddWithValue("$table", tableCode);
            query.Parameters.AddWithValue("$generation", generationId);
            query.Parameters.AddWithValue("$rank", row.Source.Candidate!.RankCode);
            query.Parameters.AddWithValue("$title", row.Source.Candidate.Title);
            query.Parameters.AddWithValue("$difficulty", row.Source.Candidate.Difficulty);
            query.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested();
    }

    private static void RequireRun(SqliteConnection connection, SqliteTransaction transaction, long run, DifficultyMatchingResult result)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT options_json FROM import_runs WHERE import_run_id=$run AND source_type='DIFFICULTY_TABLE' AND source_name=$table;";
        query.Parameters.AddWithValue("$run", run);
        query.Parameters.AddWithValue("$table", result.Input.Table.Code);
        if (query.ExecuteScalar() is not string json) throw new InvalidOperationException("監査runの出典・表・snapshotがありません。");
        using var doc = JsonDocument.Parse(json);
        var saved = doc.RootElement.GetProperty("snapshot");
        // 未解決だけでなく正常行と原本も保存済みでなければ、監査行の書込みを開始しない。
        if (!JsonNode.DeepEquals(JsonNode.Parse(saved.GetRawText()), JsonNode.Parse(CreateSnapshot(result))))
            throw new InvalidDataException("runのsnapshotと保存対象の照合結果が一致しません。");
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, long run, string entity,
        string? key, string? title, string? difficulty, int? level, int? notes, string code, string detail, string raw)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            INSERT INTO unresolved_imports(import_run_id,source_system,source_record_key,entity_type,
                raw_song_name,raw_difficulty_type,raw_level,raw_total_notes,reason_code,reason_detail,raw_data)
            VALUES($run,'DIFFICULTY_TABLE',$key,$entity,$title,$difficulty,$level,$notes,$code,$detail,$raw);
            """;
        foreach (var (name, value) in new (string, object?)[] { ("run", run), ("key", key), ("entity", entity),
            ("title", title), ("difficulty", difficulty), ("level", level), ("notes", notes), ("code", code), ("detail", detail), ("raw", raw) })
            query.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
        query.ExecuteNonQuery();
    }
}

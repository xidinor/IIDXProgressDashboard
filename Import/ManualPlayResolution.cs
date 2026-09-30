using System.Globalization;
using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Import;

public sealed record PendingImport(long Id, long RunId, string Source, string? Key, string Entity,
    string? Title, string? Difficulty, string Reason, string? Detail, string? Raw);
public sealed record ManualPlayReview(PendingImport Row, ChartResolutionRequest? Request,
    IReadOnlyList<ChartCandidate> Charts, string Explanation, string? BlockedReason);
public sealed record ManualPlayDecision(PendingImport Row, ChartCandidate Chart, string Note);

/// <summary>保存された元行だけを手動確定する。aliasや後続行の自動照合規則には波及させない。</summary>
public sealed class ManualPlayResolution(DatabaseInitializer database, string backupDirectory)
{
    public const string SourceType = "MANUAL_PLAY_RESOLUTION";

    public IReadOnlyList<PendingImport> ReadPending()
    {
        using var c = OpenReadOnly();
        using var q = Command(c, null, "SELECT * FROM unresolved_imports WHERE status='PENDING' ORDER BY unresolved_id;");
        using var r = q.ExecuteReader();
        var rows = new List<PendingImport>();
        while (r.Read()) rows.Add(ReadRow(r));
        // 再試行ごとの監査行を同じ元行として表示する。別プレイのキーは統合しない。
        return rows.GroupBy(r => (r.Source, Key: r.Key ?? "unkeyed:" + r.Id, r.Entity, r.Raw))
            .Select(g => g.First()).ToArray();
    }

    public ManualPlayReview Review(PendingImport row)
    {
        try
        {
            using var c = OpenReadOnly();
            using var t = c.BeginTransaction(deferred: true);
            var value = Decode(row);
            CheckOriginal(c, t, row);
            var request = value.Request;
            var resolution = ChartResolver.Resolve(request, c, t);
            ChartResolver.TryParseChart(request.Difficulty, request.PlayStyle, out var style, out var difficulty);
            using var q = Command(c, t, """
                SELECT c.chart_id,c.tag,s.title,c.play_style,c.difficulty,c.level,c.total_notes,s.is_active,c.is_active
                FROM charts c JOIN songs s ON s.tag=c.tag
                WHERE c.play_style=$style AND c.difficulty=$difficulty ORDER BY s.title,c.tag;
                """, ("$style", style), ("$difficulty", difficulty));
            using var r = q.ExecuteReader();
            var charts = new List<ChartCandidate>();
            while (r.Read()) charts.Add(ReadChart(r));
            var explanation = JsonSerializer.Serialize(new { request, resolution.Issues,
                resolution.Candidates, resolution.TitleEvidence, resolution.ExternalEvidence }, new JsonSerializerOptions { WriteIndented = true });
            return new(row, request, charts, explanation, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or KeyNotFoundException)
        { return new(row, null, [], row.Detail ?? "", ex.Message); }
    }

    public static string? Incompatibility(ChartResolutionRequest request, ChartCandidate chart)
    {
        if (!ChartResolver.TryParseChart(request.Difficulty, request.PlayStyle, out var style, out var difficulty)
            || chart.PlayStyle != style || chart.Difficulty != difficulty) return "SP/DP・譜面種別が一致しません。";
        if (request.Level.HasValue && chart.Level.HasValue && request.Level != chart.Level)
            return "levelが一致しません。マスター・元入力を確認してください。";
        if (request.TotalNotes.HasValue && chart.TotalNotes.HasValue && request.TotalNotes != chart.TotalNotes)
            return "Notesが一致しません。譜面改訂の扱いは別途確認が必要です。";
        return null;
    }

    /// <summary>同じ取込元ID・種別・元曲名・譜面表記の未解決プレイを、元行単位で検証して仮決定する。</summary>
    public IReadOnlyList<ManualPlayDecision> PrepareBulk(PendingImport anchor, ChartCandidate chart, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new InvalidDataException("判断理由を入力してください。");
        var sourceId = SourceIdentity(anchor);
        if (sourceId is null || anchor.Entity != "PLAY_HISTORY" || string.IsNullOrWhiteSpace(anchor.Title))
            throw new InvalidDataException("この元行は一括確定の対象にできません。");
        var rows = ReadPending().Where(row => row.Source == anchor.Source && SourceIdentity(row) == sourceId
            && row.Entity == anchor.Entity && row.Title == anchor.Title && row.Difficulty == anchor.Difficulty).ToArray();
        if (!rows.Any(row => row.Id == anchor.Id)) throw new InvalidDataException("選択中の未解決行が変更されています。画面を開き直してください。");
        if (rows.Length < 2) throw new InvalidDataException("同じ条件の未解決元行が2件以上ありません。");
        var decisions = rows.Select(row => new ManualPlayDecision(row, chart, note.Trim())).ToArray();
        using var c = OpenReadOnly();
        using var t = c.BeginTransaction(deferred: true);
        // 1件でも不正・変更済み・譜面条件の矛盾があれば、グループ全体を仮決定しない。
        foreach (var decision in decisions) Validate(c, t, decision);
        return decisions;
    }

    private static string? SourceIdentity(PendingImport row)
    {
        if (row.Key is null) return null;
        var colon = row.Key.IndexOf(':');
        if (colon <= 0 || !Guid.TryParseExact(row.Key[..colon], "N", out _)) return null;
        return row.Key[..colon];
    }

    public void Save(IReadOnlyList<ManualPlayDecision> decisions, IReadOnlyCollection<long>? bulkSelectionIds = null)
    {
        if (decisions.Count == 0) return;
        if (decisions.Select(d => (d.Row.Source, d.Row.Key)).Distinct().Count() != decisions.Count)
            throw new InvalidDataException("同じ元行の決定が重複しています。");
        if (bulkSelectionIds is not null && bulkSelectionIds.Except(decisions.Select(d => d.Row.Id)).Any())
            throw new InvalidDataException("一括確定対象が保存対象と一致しません。");
        using var c = database.OpenConnection();
        new MigrationRunner().Run(c);
        long run;
        using (var preparation = c.BeginTransaction(deferred: false))
        {
            foreach (var decision in decisions) Validate(c, preparation, decision);
            // 書込み前のバックアップが成功した場合だけ開始ログを残す。
            DatabaseBackup.Create(database, backupDirectory);
            using var start = Command(c, preparation, """
                INSERT INTO import_runs(source_type,source_name,options_json,status,records_read)
                VALUES ($type,'manual',$options,'RUNNING',$count) RETURNING import_run_id;
                """, ("$type", SourceType), ("$count", decisions.Count), ("$options", JsonSerializer.Serialize(new
                {
                    contractVersion = 1, method = "explicit-chart-selection",
                    bulkSelectionUnresolvedIds = bulkSelectionIds?.Order().ToArray() ?? [],
                    decisions = decisions.Select(d => new
                    { unresolvedId = d.Row.Id, originalImportRunId = d.Row.RunId, d.Row.Source, d.Row.Key,
                        d.Row.Reason, selectedChart = d.Chart, note = d.Note.Trim() })
                })));
            run = (long)start.ExecuteScalar()!;
            preparation.Commit();
        }
        try
        {
            using var t = c.BeginTransaction(deferred: false);
            foreach (var decision in decisions)
            {
                // 画面を開いた後の別取込・マスター更新を再確認し、古い選択を適用しない。
                var value = Validate(c, t, decision);
                var row = decision.Row;
                if (value.Legacy is { } legacy)
                    LegacyInfinitasLogImporter.InsertPlay(c, t, run, row.Key!, value.LegacyRow!, legacy, decision.Chart.ChartId);
                else RefluxSessionTsvImporter.InsertPlay(c, t, run, row.Key!, row.Raw!, value.Reflux!, decision.Chart.ChartId);
                using var update = Command(c, t, """
                    UPDATE unresolved_imports SET status='RESOLVED',resolved_chart_id=$chart,resolved_at=$now
                    WHERE status='PENDING' AND entity_type='PLAY_HISTORY' AND source_system=$source
                        AND source_record_key=$key AND raw_data=$raw;
                    """, ("$chart", decision.Chart.ChartId), ("$now", Now()), ("$source", row.Source),
                    ("$key", row.Key), ("$raw", row.Raw));
                update.ExecuteNonQuery();
            }
            using var finish = Command(c, t, """
                UPDATE import_runs SET status='SUCCESS',records_imported=$count,completed_at=$now WHERE import_run_id=$run;
                """, ("$count", decisions.Count), ("$now", Now()), ("$run", run));
            finish.ExecuteNonQuery();
            // 履歴・解決状態・成功件数を一括確定する。途中失敗では全選択を戻す。
            t.Commit();
        }
        catch (Exception ex)
        {
            using var failed = Command(c, null, """
                UPDATE import_runs SET status='FAILED',records_imported=0,message=$message,completed_at=$now WHERE import_run_id=$run;
                """, ("$message", ex.Message), ("$now", Now()), ("$run", run));
            failed.ExecuteNonQuery();
            throw;
        }
    }

    private static Decoded Validate(SqliteConnection c, SqliteTransaction t, ManualPlayDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Note)) throw new InvalidDataException("判断理由を入力してください。");
        var row = decision.Row;
        using (var q = Command(c, t, "SELECT * FROM unresolved_imports WHERE unresolved_id=$id AND status='PENDING';", ("$id", row.Id)))
        using (var r = q.ExecuteReader())
            if (!r.Read() || ReadRow(r) != row) throw new InvalidDataException("未解決行が変更・解決されています。画面を開き直してください。");
        CheckOriginal(c, t, row);
        var decoded = Decode(row);
        using var chart = Command(c, t, """
            SELECT c.chart_id,c.tag,s.title,c.play_style,c.difficulty,c.level,c.total_notes,s.is_active,c.is_active
            FROM charts c JOIN songs s ON s.tag=c.tag WHERE c.chart_id=$id;
            """, ("$id", decision.Chart.ChartId));
        using var reader = chart.ExecuteReader();
        if (!reader.Read() || ReadChart(reader) != decision.Chart)
            throw new InvalidDataException("選択した譜面のマスターが変更されています。画面を開き直してください。");
        if (Incompatibility(decoded.Request, decision.Chart) is { } error) throw new InvalidDataException(error);
        return decoded;
    }

    private static void CheckOriginal(SqliteConnection c, SqliteTransaction t, PendingImport row)
    {
        using var existing = Command(c, t, "SELECT 1 FROM play_history WHERE source_system=$source AND source_record_key=$key;",
            ("$source", row.Source), ("$key", row.Key));
        if (existing.ExecuteScalar() is not null) throw new InvalidDataException("同じ元行の履歴が既に登録されています。付替えはできません。");
        using var first = Command(c, t, """
            SELECT raw_data FROM unresolved_imports WHERE source_system=$source AND source_record_key=$key
            ORDER BY unresolved_id LIMIT 1;
            """, ("$source", row.Source), ("$key", row.Key));
        if (!Equals(first.ExecuteScalar(), row.Raw)) throw new InvalidDataException("初回の元行と内容が異なる競合です。手動照合では訂正できません。");
    }

    private sealed record Decoded(ChartResolutionRequest Request, LegacySourceRow? LegacyRow = null,
        LegacyConvertedRow? Legacy = null, RefluxConvertedRow? Reflux = null);

    private static Decoded Decode(PendingImport row)
    {
        if (row.Entity != "PLAY_HISTORY" || row.Source is not (LegacyInfinitasLogImporter.SourceSystem or RefluxSessionTsvImporter.SourceSystem))
            throw new InvalidDataException("この記録は履歴の譜面選択では解決できません。取得元・難易度表・Sessionの確認が必要です。");
        if (row.Key is null || row.Raw is null) throw new InvalidDataException("元行の識別情報がありません。");
        using var doc = JsonDocument.Parse(row.Raw);
        var root = doc.RootElement;
        Decoded result;
        if (row.Source == LegacyInfinitasLogImporter.SourceSystem)
        {
            if (root.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("未対応の旧履歴保存形式です。");
            var types = root.GetProperty("storageTypes");
            var values = root.GetProperty("row").EnumerateObject().ToDictionary(p => p.Name, p =>
                types.GetProperty(p.Name).GetString() switch
                {
                    "null" => null, "integer" => (object)p.Value.GetInt64(), "text" => p.Value.GetString(),
                    "real" when p.Value.ValueKind == JsonValueKind.Number => p.Value.GetDouble(),
                    "blob" => p.Value.GetBytesFromBase64(), _ => throw new InvalidDataException("元行に未対応の値があります。")
                });
            var value = LegacyRowConverter.Convert(values);
            if (value.Issues.Count > 0) throw new InvalidDataException(string.Join("\n", value.Issues.Select(i => i.Detail)));
            result = new(value.Request, new((long)values["id"]!, values, row.Raw), value);
        }
        else
        {
            if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("未対応のReflux保存形式です。");
            var parsed = RefluxSessionReader.Parse(Encoding.UTF8.GetBytes(root.GetProperty("header").GetString() + "\n" + root.GetProperty("rawLine").GetString() + "\n"));
            if (parsed.Rows.Count != 1) throw new InvalidDataException("保存元行が単一行ではありません。");
            if (!Enum.TryParse<RefluxTimeMode>(root.GetProperty("timeMode").GetString(), out var mode))
                throw new InvalidDataException("保存された時刻設定が不正です。");
            var value = RefluxRowConverter.Convert(parsed.Rows[0], new(mode, root.GetProperty("timeZoneId").GetString()));
            if (value.Issues.Count > 0) throw new InvalidDataException(string.Join("\n", value.Issues.Select(i => i.Detail)));
            result = new(value.Request, Reflux: value);
        }
        if (!ChartResolver.TryParseChart(result.Request.Difficulty, result.Request.PlayStyle, out _, out _))
            throw new InvalidDataException("SP/DP・譜面種別が不正です。");
        return result;
    }

    private SqliteConnection OpenReadOnly()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, ForeignKeys = true, Pooling = false }.ToString());
        try { c.Open(); return c; } catch { c.Dispose(); throw; }
    }
    private static PendingImport ReadRow(SqliteDataReader r)
    {
        string? Text(string name) => r.IsDBNull(r.GetOrdinal(name)) ? null : r.GetString(r.GetOrdinal(name));
        return new(r.GetInt64(r.GetOrdinal("unresolved_id")), r.GetInt64(r.GetOrdinal("import_run_id")),
            Text("source_system")!, Text("source_record_key"), Text("entity_type")!, Text("raw_song_name"),
            Text("raw_difficulty_type"), Text("reason_code")!, Text("reason_detail"), Text("raw_data"));
    }
    private static ChartCandidate ReadChart(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetInt64(7) == 1, r.GetInt64(8) == 1);
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] values)
    {
        var q = c.CreateCommand(); q.Transaction = t; q.CommandText = sql;
        foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return q;
    }
}

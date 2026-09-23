using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Dashboard;

/// <summary>表示は読取専用の一貫したsnapshot。曲名で履歴を結合しない。</summary>
public sealed class DashboardRepository(string path)
{
    public DashboardSnapshot Read()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        SqliteCommand Query(string sql)
        {
            var cmd = connection.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = sql; return cmd;
        }
        var histories = new Dictionary<long, List<HistoryPoint>>();
        using (var cmd = Query("SELECT play_id,chart_id,played_at,clear_lamp,score,miss_count,raw_data FROM play_history ORDER BY played_at,play_id;"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                long id = r.GetInt64(1);
                if (!histories.TryGetValue(id, out var list)) histories[id] = list = [];
                list.Add(new(r.GetInt64(0), id, list.Count + 1, r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                    r.IsDBNull(5) ? null : r.GetInt32(5), DisplayValues.IsMinute(r.IsDBNull(6) ? null : r.GetString(6))));
            }
        var charts = new List<ChartView>();
        using (var cmd = Query("SELECT c.chart_id,s.title,c.play_style,c.difficulty,c.level,c.total_notes,c.is_active AND s.is_active FROM charts c JOIN songs s ON s.tag=c.tag ORDER BY s.title,c.play_style,c.difficulty;"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) charts.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetInt32(6) != 0,
                histories.GetValueOrDefault(r.GetInt64(0)) ?? []));
        var tables = new List<TableView>();
        using (var cmd = Query("SELECT table_id,table_code,display_name FROM difficulty_tables WHERE is_active=1 ORDER BY level,gauge_type DESC;"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) tables.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), [], new Dictionary<long,string>(), new HashSet<long>()));
        for (int i = 0; i < tables.Count; i++)
        {
            var table = tables[i]; var ranks = new List<RankView>(); var entries = new Dictionary<long,string>(); var retained = new HashSet<long>();
            using (var cmd = Query("SELECT rank_code,display_name,sort_order FROM difficulty_ranks WHERE table_id=$id ORDER BY sort_order DESC,rank_code;"))
            {
                cmd.Parameters.AddWithValue("$id", table.Id); using var r = cmd.ExecuteReader();
                while (r.Read()) ranks.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2)));
            }
            using (var cmd = Query("SELECT chart_id,rank_code FROM difficulty_table_entries WHERE table_id=$id;"))
            {
                cmd.Parameters.AddWithValue("$id", table.Id); using var r = cmd.ExecuteReader();
                while (r.Read()) entries.Add(r.GetInt64(0), r.GetString(1));
            }
            // 最新の反映済み世代を使い、取得失敗runを評価の更新と取り違えない。
            using (var cmd = Query("SELECT options_json FROM import_runs WHERE source_type='DIFFICULTY_TABLE' AND source_name=$code AND status IN ('SUCCESS','PARTIAL') AND json_extract(options_json,'$.stateCommitted')=1 ORDER BY import_run_id DESC LIMIT 1;"))
            {
                cmd.Parameters.AddWithValue("$code", table.Code);
                if (cmd.ExecuteScalar() is string json)
                {
                    using var doc = JsonDocument.Parse(json);
                    foreach (var id in doc.RootElement.GetProperty("difficultyState").GetProperty("retainedChartIds").EnumerateArray()) retained.Add(id.GetInt64());
                }
            }
            tables[i] = table with { Ranks = ranks, Entries = entries, Retained = retained };
        }
        using var pending = Query("SELECT count(*) FROM unresolved_imports WHERE status='PENDING';");
        return new(charts, tables, Convert.ToInt32(pending.ExecuteScalar()));
    }
}

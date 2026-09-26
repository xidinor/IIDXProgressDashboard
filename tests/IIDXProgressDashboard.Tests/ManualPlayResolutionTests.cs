using IIDXProgressDashboard.Dashboard;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Import;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class ManualPlayResolutionTests
{
    [Fact]
    public async Task BulkSelectionKeepsEachPlayAndStaysWithinSourceAndChartKind()
    {
        // 同条件の別元行だけを選び、別移行元・別曲名・別譜面を巻き込まない。
        var directory = Path.Combine(Path.GetTempPath(), "IIDXManualBulkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "new.db");
            var database = new DatabaseInitializer(path); database.Initialize();
            Execute(path, """
                INSERT INTO songs(tag,title,normalized_title) VALUES ('selected','Selected Song','SELECTED SONG');
                INSERT INTO charts(chart_id,tag,play_style,difficulty,level,total_notes) VALUES (1,'selected','SP','A',11,1000);
                """);
            var source = Path.Combine(directory, "old.db");
            Execute(source, """
                CREATE TABLE play_history(id INTEGER PRIMARY KEY,level TEXT,song_name TEXT,difficulty_type TEXT,total_notes INTEGER,
                    clear_type TEXT,score INTEGER,miss_count INTEGER,played_option TEXT,played_at TEXT,original_data TEXT);
                INSERT INTO play_history VALUES
                (1,'11','External Name','SPA',1000,'CLEAR',1500,20,NULL,'2026-09-26-12-34','a'),
                (2,'11','External Name','SPA',1000,'H-CLEAR',1600,10,NULL,'2026-09-26-12-35','b'),
                (3,'11','Another Name','SPA',1000,'CLEAR',1700,5,NULL,'2026-09-26-12-36','c'),
                (4,'11','External Name','SPH',1000,'CLEAR',1400,30,NULL,'2026-09-26-12-37','d');
                """);
            var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
            var importer = new LegacyInfinitasLogImporter(database, directory);
            Assert.Equal(4, (await importer.ImportAsync(source, firstId)).Unresolved);
            Assert.Equal(4, (await importer.ImportAsync(source, secondId)).Unresolved);
            var service = new ManualPlayResolution(database, directory);
            var anchor = service.ReadPending().Single(r => r.Key == firstId.ToString("N") + ":1");
            var chart = service.Review(anchor).Charts.Single(c => c.ChartId == 1);
            var before = File.ReadAllBytes(path);
            var decisions = service.PrepareBulk(anchor, chart, "同一取込元の各譜面条件を確認");
            Assert.Equal(new[] { firstId.ToString("N") + ":1", firstId.ToString("N") + ":2" },
                decisions.Select(d => d.Row.Key));
            Assert.Equal(before, File.ReadAllBytes(path));
            service.Save(decisions, decisions.Select(d => d.Row.Id).ToArray());
            var snapshot = new DashboardRepository(path).Read();
            Assert.Equal(new[] { 1500, 1600 }, snapshot.Charts.Single(c => c.ChartId == 1).History.Select(p => p.Score));
            Assert.Equal(6, snapshot.Pending);
            using var c = database.OpenConnection(); using var q = c.CreateCommand();
            q.CommandText = "SELECT records_imported FROM import_runs WHERE source_type='MANUAL_PLAY_RESOLUTION' AND status='SUCCESS';";
            Assert.Equal(2L, q.ExecuteScalar());
            q.CommandText = "SELECT json_array_length(json_extract(options_json,'$.bulkSelectionUnresolvedIds')) FROM import_runs WHERE source_type='MANUAL_PLAY_RESOLUTION' AND status='SUCCESS';";
            Assert.Equal(2L, q.ExecuteScalar());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualSelectionFeedsDashboardAndKeepsSourceIdentity(bool reflux)
    {
        // 実Importerで未解決を作り、画面と同じサービスから選択・保存して表示接合を確認する。
        var directory = Path.Combine(Path.GetTempPath(), "IIDXManualTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "new.db");
            var database = new DatabaseInitializer(path); database.Initialize();
            Execute(path, """
                INSERT INTO songs(tag,title,normalized_title) VALUES ('selected','Selected Song','SELECTED SONG'),('other','Other','OTHER');
                INSERT INTO charts(chart_id,tag,play_style,difficulty,level,total_notes) VALUES
                    (1,'selected','SP','A',11,1000),(2,'other','SP','A',11,900);
                """);
            var source = Path.Combine(directory, reflux ? "Session.tsv" : "old.db");
            var id = Guid.NewGuid();
            if (reflux)
                File.WriteAllText(source, "title\tdifficulty\tlamp\texscore\tmisscount\tnotecount\tlevel\tstyle\tdate\nExternal Name\tSPA\tHC\t1500\t-\t1000\t11\tRANDOM\t2026/09/26 12:34:56\n");
            else Execute(source, """
                CREATE TABLE play_history(id INTEGER PRIMARY KEY,level TEXT,song_name TEXT,difficulty_type TEXT,total_notes INTEGER,
                    clear_type TEXT,score INTEGER,miss_count INTEGER,played_option TEXT,played_at TEXT,original_data TEXT);
                INSERT INTO play_history VALUES(1,'11','External Name','SPA',1000,'H-CLEAR',1500,NULL,'RANDOM','2026-09-26-12-34','original');
                """);
            async Task<int> ImportAgain()
            {
                if (reflux) return (await new RefluxSessionTsvImporter(database, directory).ImportAsync(source, id)).Imported;
                return (await new LegacyInfinitasLogImporter(database, directory).ImportAsync(source, id)).Imported;
            }
            var original = File.ReadAllBytes(source);
            Assert.Equal(0, await ImportAgain());
            Assert.Equal(0, await ImportAgain());
            var service = new ManualPlayResolution(database, directory);
            var beforeRead = File.ReadAllBytes(path);
            var pending = Assert.Single(service.ReadPending());
            var review = service.Review(pending);
            Assert.Null(review.BlockedReason);
            // 閲覧・選択だけで終了するキャンセル経路はDBを書き換えない。
            var selected = review.Charts.Single(c => c.ChartId == 1);
            Assert.Equal(beforeRead, File.ReadAllBytes(path));
            Assert.Empty(new DashboardRepository(path).Read().Charts.Single(c => c.ChartId == 1).History);
            // Notes矛盾の候補を選んでも保存を開始せず、無変更のまま拒否する。
            Assert.Throws<InvalidDataException>(() => service.Save([new(pending, review.Charts.Single(c => c.ChartId == 2), "誤った選択")]));
            Assert.Equal(beforeRead, File.ReadAllBytes(path));
            service.Save([new(pending, selected, "元曲名と譜面情報を確認した")]);
            var snapshot = new DashboardRepository(path).Read();
            var history = Assert.Single(snapshot.Charts.Single(c => c.ChartId == 1).History);
            Assert.Equal(1500, history.Score); Assert.Equal(5, history.Lamp); Assert.Null(history.MissCount);
            Assert.Equal("RANDOM", history.Options); Assert.Equal(!reflux, history.MinutePrecision);
            Assert.Equal(reflux ? "2026-09-26T12:34:56Z" : "2026-09-26T03:34:00Z", history.PlayedAt);
            Assert.Equal(0, snapshot.Pending); Assert.Empty(service.ReadPending());
            Assert.Throws<InvalidDataException>(() => service.Save([new(pending, selected, "二重操作")]));
            Assert.Equal(0, await ImportAgain());
            Assert.Single(new DashboardRepository(path).Read().Charts.Single(c => c.ChartId == 1).History);
            Assert.Equal(original, File.ReadAllBytes(source));
            using var c = database.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT raw_data FROM play_history;"; Assert.Equal(pending.Raw, q.ExecuteScalar());
            q.CommandText = "SELECT count(*) FROM unresolved_imports WHERE status='RESOLVED' AND resolved_chart_id=1;"; Assert.Equal(2L, q.ExecuteScalar());
            q.CommandText = "SELECT options_json FROM import_runs WHERE source_type='MANUAL_PLAY_RESOLUTION' AND status='SUCCESS';";
            Assert.Contains("selectedChart", Assert.IsType<string>(q.ExecuteScalar()));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Execute(string path, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery();
    }
}

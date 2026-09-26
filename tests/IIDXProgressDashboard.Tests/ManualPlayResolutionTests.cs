using IIDXProgressDashboard.Dashboard;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Import;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class ManualPlayResolutionTests
{
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

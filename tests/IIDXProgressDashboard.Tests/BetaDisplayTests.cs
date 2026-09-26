using IIDXProgressDashboard.Dashboard;
using IIDXProgressDashboard.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class BetaDisplayTests
{
    [Fact]
    public async Task LegacyPreparationFeedsChartScopedLatestAndBestWithoutChangingInput()
    {
        // 取込内部の再検証ではなく、準備→実Importer→表示Repositoryの接合点を一本で確認する。
        var directory = Path.Combine(Path.GetTempPath(), "IIDXBetaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var seed = Path.Combine(directory, "seed.db"); new DatabaseInitializer(seed).Initialize();
            Execute(seed, """
                INSERT INTO songs(tag,title,normalized_title) VALUES ('song','Song','SONG'),('empty','Empty','EMPTY');
                INSERT INTO charts(chart_id,tag,play_style,difficulty,level,total_notes) VALUES
                  (1,'song','SP','A',11,1000),(2,'song','DP','A',11,1000),(3,'empty','SP','B',1,NULL);
                INSERT INTO difficulty_tables(table_id,table_code,display_name,level,play_style,gauge_type,source_name)
                  VALUES (1,'TEST','Test',11,'SP','NORMAL','fixture');
                INSERT INTO difficulty_ranks VALUES (1,'A','地力A','JIRIKI',1);
                INSERT INTO difficulty_table_entries(table_id,chart_id,rank_code) VALUES (1,1,'A'),(1,3,'A');
                """);
            var legacy = Path.Combine(directory, "old.db");
            Execute(legacy, """
                CREATE TABLE play_history (id INTEGER PRIMARY KEY,played_at TEXT,song_name TEXT,difficulty_type TEXT,
                level TEXT,total_notes INTEGER,score INTEGER,miss_count INTEGER,clear_type TEXT,played_option TEXT,original_data TEXT);
                INSERT INTO play_history VALUES
                (1,'2026-01-01-12-00','Song','SPA','11',1000,1800,0,'H-CLEAR','RANDOM','original 1'),
                (2,'2026-01-01-12-00','Song','SPA','11',1000,1200,NULL,'FAILED',NULL,'original 2'),
                (3,'2026-01-01-12-01','Song','DPA','11',1000,1000,20,'NO PLAY',NULL,'original 3'),
                (4,'2026-01-01-12-02','Missing','SPA','11',1000,800,10,'FAILED',NULL,'unresolved');
                """);
            var before = File.ReadAllBytes(legacy);
            var target = Path.Combine(directory, "beta");
            var result = await BetaPreparation.PrepareAsync(target, seed, legacy);
            Assert.Equal(3, result.Imported); Assert.Equal(1, result.Unresolved);
            Assert.Equal(before, File.ReadAllBytes(legacy));
            var settings = BetaSettings.Read(target);
            var snapshot = new DashboardRepository(settings.DatabasePath).Read();
            var sp = snapshot.Charts.Single(c => c.ChartId == 1);
            Assert.Equal(5, sp.BestLamp); Assert.Equal(1, sp.Latest!.Lamp);
            Assert.Equal(1800, sp.BestScore); Assert.Equal(1200, sp.Latest.Score);
            Assert.Equal(0, sp.MinimumBp); Assert.Null(sp.Latest.MissCount);
            Assert.Equal(new[] { 1, 2 }, sp.History.Select(p => p.Number));
            Assert.All(sp.History, p => Assert.True(p.MinutePrecision));
            Assert.Equal("2026-01-01T03:00:00Z", sp.History[0].PlayedAt);
            Assert.Equal("RANDOM", sp.History[0].Options);
            Assert.Equal("—", sp.History[1].Options);
            Assert.Equal(0, snapshot.Charts.Single(c => c.ChartId == 2).BestLamp);
            Assert.Null(snapshot.Charts.Single(c => c.ChartId == 3).BestLamp);
            Assert.Equal(1, snapshot.Pending); Assert.Equal(2, Assert.Single(snapshot.Tables).Entries.Count);
            Assert.Single(DisplayValues.Visible(sp.History, true));
            var points = new[] { sp.History[0], sp.History[1], sp.History[0] with { Number = 3 } };
            Assert.Equal(new[] { 1, 3 }, DisplayValues.Visible(points, true).Select(p => p.Number));
            // 同じ配置先での準備し直しは拒否し、GUID・履歴を保持する。
            await Assert.ThrowsAsync<IOException>(() => BetaPreparation.PrepareAsync(target, seed, legacy));
            Assert.Equal(settings.SourceId, BetaSettings.Read(target).SourceId);

            // Beta2は既存Importer内部を再検証せず、旧履歴→Reflux→一覧/グラフ共通モデルを確認。
            var tsv = Path.Combine(directory, "Session.tsv");
            const string header = "title\tdifficulty\tlamp\texscore\tmisscount\tstyle\tgauge\tdate\n";
            const string row = "Song\tSPA\tEC\t1500\t-\tRANDOM\tEASY\t2026/09/23 12:34:56\n";
            File.WriteAllText(tsv, header + row);
            var reflux = new BetaRefluxImport(settings);
            var imported = await reflux.ImportAsync(tsv, null, new());
            Assert.Equal("SUCCESS", imported.Status); Assert.Equal(1, imported.Imported);
            var saved = Assert.Single(new BetaRefluxImport(settings).ReadSessions());
            var duplicate = await reflux.ImportAsync(tsv, null, new());
            Assert.Equal(1, duplicate.Duplicates); Assert.Equal(0, duplicate.Imported);
            var renamed = Path.Combine(directory, "Renamed.tsv");
            File.Copy(tsv, renamed);
            File.AppendAllText(renamed, "Song\tSPA\tHC\t1900\t4\tMIRROR\tHARD\t2026/09/23 12:35:56\n");
            var appended = await reflux.ImportAsync(renamed, saved.Id, saved.Options);
            Assert.Equal(1, appended.Imported); Assert.Equal(1, appended.Duplicates);
            Assert.Equal(2, (await reflux.ImportAsync(renamed, null, saved.Options)).Duplicates);
            sp = new DashboardRepository(settings.DatabasePath).Read().Charts.Single(c => c.ChartId == 1);
            Assert.Equal(4, sp.History.Count); Assert.Equal(1900, sp.Latest!.Score); Assert.Equal(1900, sp.BestScore);
            Assert.Equal(0, sp.MinimumBp); Assert.Null(sp.History[2].MissCount);
            Assert.False(sp.History[2].MinutePrecision); Assert.Equal("2026-09-23T12:34:56Z", sp.History[2].PlayedAt);
            Assert.Equal("RANDOM", sp.History[2].Options);
            Assert.Equal("MIRROR", sp.History[3].Options);
            Assert.Equal(before, File.ReadAllBytes(legacy));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RepeatedHistoryFilterKeepsFirstOfSameConditionWithinTwoMinutes()
    {
        // 同条件の記録が間に別条件を挟んでも、時刻順の最初だけを表示する。
        HistoryPoint Point(int number, string time, int score = 1934, int? bp = 130, string options = "OFF") =>
            new(number, 1, number, time, 3, score, bp, true, options);
        var history = new[]
        {
            Point(1, "2026-07-02T12:48:00Z"),
            Point(2, "2026-07-02T12:49:00Z"),
            Point(3, "2026-07-02T12:50:00Z", score: 1500),
            Point(4, "2026-07-02T12:51:00Z"),
            Point(5, "2026-07-02T12:53:00Z", options: "RANDOM"),
            Point(6, "2026-07-02T12:54:00Z", bp: null),
            Point(7, "2026-07-02T12:56:01Z"),
            Point(8, "2026-07-03T12:56:00Z"),
        };
        Assert.Equal(8, DisplayValues.Visible(history, false).Length);
        Assert.Equal(new[] { 1, 3, 5, 6, 7, 8 },
            DisplayValues.Visible(history, false, true).Select(p => p.Number));
        Assert.Equal(new[] { 1, 3, 5, 7, 8 },
            DisplayValues.Visible(history, true, true).Select(p => p.Number));
        Assert.Equal(8, history.Length);
    }

    [Fact]
    public void ScoreBoundariesUseIntegerComparisonAndDisplayRoundingOnly()
    {
        // 小さなループで全境界の直前・到達と、分母の剰余を確認する。
        string[] levels = ["F", "E", "D", "C", "B", "A", "AA", "AAA"];
        for (int notes = 1000; notes <= 1008; notes++)
            for (int k = 2; k <= 8; k++)
            {
                int threshold = (int)((notes * 2L * k + 8) / 9);
                Assert.Equal(levels[k - 1], DisplayValues.Score(threshold, notes).DjLevel);
                Assert.Equal(levels[k - 2], DisplayValues.Score(threshold - 1, notes).DjLevel);
            }
        Assert.Equal(("F", "0.00%"), DisplayValues.Score(0, 1000));
        Assert.Equal(("AAA", "100.00%"), DisplayValues.Score(2000, 1000));
        Assert.Equal("87.13%", DisplayValues.Score(6970, 4000).Rate);
        Assert.Contains("不完全", DisplayValues.Score(1000, null).DjLevel);
        Assert.Contains("不完全", DisplayValues.Score(0, 0).DjLevel);
        Assert.Contains("範囲外", DisplayValues.Score(2001, 1000).DjLevel);
    }

    [Fact]
    public void IniIsStrictAndDoesNotOverwriteExistingSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IIDXBetaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Throws<FileNotFoundException>(() => BetaSettings.Read(directory));
            var settings = new BetaSettings(directory, "current.db", "old.db", Guid.NewGuid(), false);
            settings.Create(); Assert.Equal(settings, BetaSettings.Read(directory));
            Assert.Throws<IOException>(() => settings.Create());
            File.WriteAllText(Path.Combine(directory, BetaSettings.FileName), "[Beta]\nDatabase=../data.db");
            Assert.Throws<InvalidDataException>(() => BetaSettings.Read(directory));
            Assert.Throws<SqliteException>(() => new DashboardRepository(Path.Combine(directory, "missing.db")).Read());
            Assert.False(File.Exists(Path.Combine(directory, "missing.db")));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}

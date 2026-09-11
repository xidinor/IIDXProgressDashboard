using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Master;
using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests;

/// <summary>合成入力→Parser→変更案→DBの公開境界でデータ保全を検証する。</summary>
public sealed class MasterUpdateServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private DatabaseInitializer Database => new(Path.Combine(directory, "output.db"));
    private MasterUpdateService Service => new(Database, Path.Combine(directory, "backups"));

    public MasterUpdateServiceTests() => Database.Initialize();

    [Fact]
    public async Task SharedNormalizationAndManualAliasSurviveRenameWithoutAutomaticOldAlias()
    {
        // Provider由来の原表記を保存し、外部入力とaliasが共通の規則で候補に到達する。
        await Service.ApplyAsync(await Prepare(Snapshot(["A"], "Ａ Song")));
        var aliases = new SongAliasRepository(Database, Path.Combine(directory, "backups"));
        aliases.Register("A", "Ｍanual Name");
        Assert.Equal("A", Assert.Single(aliases.FindCandidates(" a　song ").Tags));
        Assert.Equal("Ａ Song", Scalar("SELECT title FROM songs;"));
        Assert.Equal("A SONG", Scalar("SELECT normalized_title FROM songs;"));
        await Service.ApplyAsync(await Prepare(Snapshot(["A"], "New Name")));
        Assert.Equal("A", Assert.Single(aliases.FindCandidates("manual name", "reflux").Tags));
        Assert.Equal("A", Assert.Single(aliases.FindCandidates("Ｎｅｗ name").Tags));
        Assert.Equal(SongTitleMatchStatus.NotFound, aliases.FindCandidates("A Song").Status);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM song_aliases;"));
        Assert.Contains(TitleNormalizer.Version, (string)Scalar("SELECT options_json FROM import_runs ORDER BY import_run_id DESC LIMIT 1;")!);
    }

    [Fact]
    public async Task UpsertPreservesIdentityHistoryAliasesAndDifficultyReferences()
    {
        var first = await Service.ApplyAsync(await Prepare(Snapshot("A", "B")));
        SeedReferences();
        var id = Scalar("SELECT chart_id FROM charts WHERE tag='A';");
        var created = Scalar("SELECT created_at FROM charts WHERE tag='A';");
        var updated = await Service.ApplyAsync(await Prepare(Snapshot(["A", "B"], "changed ' title", 12, 1800)));
        Assert.Equal(id, Scalar("SELECT chart_id FROM charts WHERE tag='A';"));
        Assert.Equal(created, Scalar("SELECT created_at FROM charts WHERE tag='A';"));
        Assert.Equal("changed ' title", Scalar("SELECT title FROM songs WHERE tag='A';"));
        Assert.Equal("CHANGED ' TITLE", Scalar("SELECT normalized_title FROM songs WHERE tag='A';"));
        Assert.Equal(12L, Scalar("SELECT level FROM charts WHERE tag='A';"));
        Assert.Equal(1800L, Scalar("SELECT total_notes FROM charts WHERE tag='A';"));
        AssertReferences();
        Assert.Equal(2, first.SongsUpserted);
        Assert.Equal(4L, Scalar($"SELECT records_imported FROM import_runs WHERE import_run_id={updated.ImportRunId};"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM charts;"));

        // バックアップを別の新規パスへ復旧し、更新直前の値と参照が読める。
        var restored = new DatabaseInitializer(Path.Combine(directory, "restored.db"));
        File.Copy(updated.BackupPath, restored.DatabasePath);
        restored.Initialize();
        using var connection = restored.OpenConnection();
        Assert.Equal("A", Scalar(connection, "SELECT title FROM songs WHERE tag='A';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM play_history;"));
        Assert.Null(Scalar(connection, "PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task MissingItemsRequireConfirmationAndReappearWithSameIdentity()
    {
        await Service.ApplyAsync(await Prepare(Snapshot("A", "B", "C", "D")));
        var id = Scalar("SELECT chart_id FROM charts WHERE tag='D';");
        var partial = await Prepare(Snapshot("A", "B", "C"));
        Assert.Equal(new[] { "D" }, partial.MissingSongs);
        Assert.Equal(new MasterChartKey("D", "SP", "A"), Assert.Single(partial.MissingCharts));
        await Service.ApplyAsync(partial);
        Assert.Equal(1L, Scalar("SELECT is_active FROM songs WHERE tag='D';"));
        // 未確認でUPSERTした後も、以前に管理したDの所有範囲は失われない。
        var confirmed = await Service.ApplyAsync(await Prepare(Snapshot("A", "B", "C")), confirmMissing: true);
        Assert.Equal(1, confirmed.SongsDeactivated);
        Assert.Equal(1, confirmed.ChartsDeactivated);
        Assert.Equal(0L, Scalar("SELECT is_active FROM charts WHERE tag='D';"));
        await Service.ApplyAsync(await Prepare(Snapshot("A", "B", "C", "D")));
        Assert.Equal(id, Scalar("SELECT chart_id FROM charts WHERE tag='D';"));
        Assert.Equal(1L, Scalar("SELECT is_active FROM charts WHERE tag='D';"));
        Assert.Equal(1L, Scalar("SELECT is_active FROM songs WHERE tag='D';"));
    }

    [Fact]
    public async Task ExistingUnknownAndOutOfScopeItemsAreNeverClaimedForRemoval()
    {
        Execute("INSERT INTO songs(tag,title,normalized_title) VALUES ('A','old','old'),('external','outside','outside'); INSERT INTO charts(tag,play_style,difficulty) VALUES ('A','SP','A');");
        await Service.ApplyAsync(await Prepare(Snapshot("A", "B")));
        // Bは所有曲だが、後から範囲外のDP譜面が追加されたため曲を無効化できない。
        Execute("INSERT INTO charts(tag,play_style,difficulty) VALUES ('B','DP','L');");
        var plan = await Prepare(Snapshot("C"));
        Assert.Empty(plan.MissingSongs);
        Assert.Equal(new MasterChartKey("B", "SP", "A"), Assert.Single(plan.MissingCharts));
        await Service.ApplyAsync(plan, confirmMissing: true);
        Assert.Equal(1L, Scalar("SELECT is_active FROM songs WHERE tag='external';"));
        Assert.Equal(1L, Scalar("SELECT is_active FROM charts WHERE tag='A';"));
        Assert.Equal(1L, Scalar("SELECT is_active FROM songs WHERE tag='B';"));
        Assert.Equal(1L, Scalar("SELECT is_active FROM charts WHERE tag='B' AND play_style='DP';"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MidWriteFailureOrCancellationRollsBackAllMasterChanges(bool cancel)
    {
        await Service.ApplyAsync(await Prepare(Snapshot("A", "B")));
        SeedReferences();
        var before = Scalar("SELECT updated_at FROM songs WHERE tag='A';");
        using var cancellation = new CancellationTokenSource();
        var plan = await Prepare(Snapshot(["A"], "changed"));
        // 最初のSQL更新が実行された直後に中断する。開始前キャンセルだけでは原子性を検証できない。
        var progress = new InlineProgress(_ =>
        {
            if (cancel) cancellation.Cancel();
            else throw new InvalidOperationException("synthetic failure after first write");
        });
        var error = await Record.ExceptionAsync(() => Service.ApplyAsync(plan, true, cancellation.Token, progress));
        if (cancel) Assert.IsType<OperationCanceledException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("A", Scalar("SELECT title FROM songs WHERE tag='A';"));
        Assert.Equal(before, Scalar("SELECT updated_at FROM songs WHERE tag='A';"));
        Assert.Equal(1L, Scalar("SELECT is_active FROM songs WHERE tag='B';"));
        Assert.Equal("FAILED", Scalar("SELECT status FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        Assert.Equal(0L, Scalar("SELECT records_imported FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        AssertReferences();
    }

    [Theory]
    [InlineData("network")]
    [InlineData("parse")]
    [InlineData("subset")]
    [InlineData("cancel")]
    public async Task AcquisitionAndValidationFailuresOnlyRecordFailure(string kind)
    {
        await Service.ApplyAsync(await Prepare(Snapshot("A", "B")));
        await Assert.ThrowsAnyAsync<Exception>(() => Service.PrepareAsync(_ =>
        {
            if (kind == "network") throw new HttpRequestException("synthetic network failure");
            if (kind == "cancel") throw new OperationCanceledException();
            if (kind == "parse") return Task.FromResult(new TextageMasterParser().Parse(new(new Dictionary<string, TextageSourceFile>())));
            var full = Snapshot("A", "B");
            return Task.FromResult(full with { Songs = full.Songs.Take(1).ToArray(), Charts = full.Charts.Take(1).ToArray() });
        }));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM songs WHERE is_active=1;"));
        Assert.Equal("FAILED", Scalar("SELECT status FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        Assert.Equal(0L, Scalar("SELECT records_read FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
    }

    [Fact]
    public async Task StaleApprovalCannotDeactivateNewerMaster()
    {
        await Service.ApplyAsync(await Prepare(Snapshot("A", "B")));
        var stale = await Prepare(Snapshot("A"));
        await Service.ApplyAsync(await Prepare(Snapshot("A", "B", "C")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ApplyAsync(stale, true));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM songs WHERE is_active=1;"));
        Assert.Equal("FAILED", Scalar("SELECT status FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
    }

    [Fact]
    public async Task BackupFailureLeavesEvenRunLogUntouched()
    {
        var plan = await Prepare(Snapshot("A"));
        var blocked = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blocked, "original");
        var service = new MasterUpdateService(Database, blocked);
        await Assert.ThrowsAnyAsync<IOException>(() => service.ApplyAsync(plan));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM import_runs;"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM songs;"));
        Assert.Equal("original", File.ReadAllText(blocked));
    }

    [Fact]
    public async Task UnknownSchemaIsNotInitializedOrLoggedInto()
    {
        var unknown = new DatabaseInitializer(Path.Combine(directory, "unknown.db"));
        using (var connection = new SqliteConnection($"Data Source={unknown.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE original(value TEXT); INSERT INTO original VALUES ('keep');";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(unknown.DatabasePath);
        var service = new MasterUpdateService(unknown, Path.Combine(directory, "backups"));
        await Assert.ThrowsAsync<AggregateException>(() => service.PrepareAsync(_ => Task.FromResult(Snapshot("A"))));
        Assert.Equal(before, File.ReadAllBytes(unknown.DatabasePath));
    }

    [Fact]
    public async Task BackupIncludesCommittedWalData()
    {
        // WALを保持する接続がある状態でもファイルコピーと違い履歴まで保存できる。
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteScalar();
        await Service.ApplyAsync(await Prepare(Snapshot("A")));
        SeedReferences();
        var result = await Service.ApplyAsync(await Prepare(Snapshot(["A"], "new")));
        using var backup = new SqliteConnection($"Data Source={result.BackupPath};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(1L, Scalar(backup, "SELECT COUNT(*) FROM play_history;"));
        Assert.Equal("A", Scalar(backup, "SELECT title FROM songs;"));
    }

    [Fact]
    public async Task AllTenChartKeysAndNullableMetadataRoundTrip()
    {
        var snapshot = TextageMasterParserTests.Parse(TextageMasterParserTests.Fixture());
        await Service.ApplyAsync(await Prepare(snapshot));
        Assert.Equal(10L, Scalar("SELECT COUNT(*) FROM charts;"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM charts WHERE difficulty='B';"));
        var fixture = TextageMasterParserTests.Fixture();
        fixture["actbl.js"] = fixture["actbl.js"].Replace("1,7,2,7", "1,1,2,7");
        fixture["datatbl.js"] = fixture["datatbl.js"].Replace("999,100", "999,0");
        await Service.ApplyAsync(await Prepare(TextageMasterParserTests.Parse(fixture)));
        Assert.Equal(DBNull.Value, Scalar("SELECT level FROM charts WHERE play_style='SP' AND difficulty='B';"));
        Assert.Equal(DBNull.Value, Scalar("SELECT total_notes FROM charts WHERE play_style='SP' AND difficulty='B';"));
    }

    [Fact]
    public async Task ExactRepeatAndReferencedRemovalNeverRecreateRows()
    {
        var snapshot = Snapshot("A", "B");
        await Service.ApplyAsync(await Prepare(snapshot));
        SeedReferences();
        var id = Scalar("SELECT chart_id FROM charts WHERE tag='A';");
        await Service.ApplyAsync(await Prepare(snapshot));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM charts;"));
        await Service.ApplyAsync(await Prepare(Snapshot("B")), true);
        Assert.Equal(0L, Scalar("SELECT is_active FROM songs WHERE tag='A';"));
        AssertReferences();
        await Service.ApplyAsync(await Prepare(snapshot));
        Assert.Equal(id, Scalar("SELECT chart_id FROM charts WHERE tag='A';"));
        Assert.Equal(1L, Scalar("SELECT is_active FROM songs WHERE tag='A';"));
        AssertReferences();
    }

    [Fact]
    public async Task SourceMutationAfterPreparationCannotChangeApprovedPlan()
    {
        var snapshot = Snapshot("A", "B");
        var plan = await Prepare(snapshot);
        // 呼出し側が保持する元入力辞書を後から破壊しても、検証済み変更案は独立している。
        ((Dictionary<string, TextageSourceFile>)snapshot.Sources.Files).Clear();
        await Service.ApplyAsync(plan);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM songs;"));
        Assert.Equal("A", Scalar("SELECT title FROM songs WHERE tag='A';"));
    }

    [Fact]
    public async Task UnknownOwnershipFormatFailsClosed()
    {
        await Service.ApplyAsync(await Prepare(Snapshot("A", "B")));
        Execute("UPDATE import_runs SET options_json='{}' WHERE status='SUCCESS';");
        await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(Snapshot("A")));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM songs WHERE is_active=1;"));
    }

    private Task<MasterUpdatePlan> Prepare(MasterSnapshot snapshot) => Service.PrepareAsync(_ => Task.FromResult(snapshot));
    private static MasterSnapshot Snapshot(params string[] tags) => Snapshot(tags, null);
    private static MasterSnapshot Snapshot(string[] tags, string? title, int level = 11, int notes = 1000)
    {
        // 文字列はJSONとしてescapeし、合成JSのquote境界を維持する。
        string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);
        var data = new Dictionary<string, string>
        {
            ["titletbl.js"] = "titletbl={" + string.Join(",", tags.Select(t => $"{Quote(t)}:[1,1,0,'genre','artist',{Quote(title ?? t)}]")) + "};",
            ["scrlist.js"] = "vertbl=['CS','1st'];",
            ["actbl.js"] = "actbl={" + string.Join(",", tags.Select(t => $"{Quote(t)}:[1,0,0,0,0,0,0,0,0,{level},7,0,0,0,0,0,0,0,0,0,0,0,0]")) + "};",
            ["datatbl.js"] = "datatbl={" + string.Join(",", tags.Select(t => $"{Quote(t)}:[0,0,0,0,{notes},0,0,0,0,0,0,'120']")) + "};"
        };
        return TextageMasterParserTests.Parse(data);
    }

    private void SeedReferences() => Execute("""
        INSERT INTO song_aliases(tag,alias_title,normalized_alias) VALUES ('A','manual','manual');
        INSERT INTO play_history(chart_id,played_at,clear_lamp,score,miss_count,source_system,source_record_key,import_run_id)
        SELECT chart_id,'2026-01-01T00:00:00Z',0,100,NULL,'synthetic','1',1 FROM charts WHERE tag='A';
        INSERT INTO difficulty_tables(table_id,table_code,display_name,level,play_style,gauge_type,source_name)
        VALUES (1,'test','test',11,'SP','NORMAL','test');
        INSERT INTO difficulty_ranks(table_id,rank_code,display_name,rank_kind,sort_order) VALUES (1,'B','B','JIRIKI',1);
        INSERT INTO difficulty_table_entries(table_id,chart_id,rank_code) SELECT 1,chart_id,'B' FROM charts WHERE tag='A';
        """);
    private void AssertReferences()
    {
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM play_history h JOIN charts c USING(chart_id) WHERE c.tag='A' AND h.miss_count IS NULL AND h.clear_lamp=0 AND h.score=100;"));
        Assert.Equal("manual", Scalar("SELECT alias_title FROM song_aliases WHERE tag='A';"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM difficulty_table_entries e JOIN charts c USING(chart_id) WHERE c.tag='A';"));
        Assert.Null(Scalar("PRAGMA foreign_key_check;"));
    }
    private void Execute(string sql)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private object? Scalar(string sql) { using var connection = Database.OpenConnection(); return Scalar(connection, sql); }
    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
    private sealed class InlineProgress(Action<int> action) : IProgress<int> { public void Report(int value) => action(value); }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

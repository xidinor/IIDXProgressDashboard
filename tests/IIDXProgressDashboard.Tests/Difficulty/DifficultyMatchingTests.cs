using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Difficulty;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests.Difficulty;

public sealed class DifficultyMatchingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private DatabaseInitializer Database => new(Path.Combine(directory, "output.db"));
    private DifficultyMatchingService Service => new(Database);
    private const string TableCode = "IIDX_SP12_GITHUB_SP12_NORMAL";

    public DifficultyMatchingTests()
    {
        Database.Initialize();
        Execute("""
            INSERT INTO songs(tag,title,normalized_title) VALUES ('synth','Synthetic & (mix)','SYNTHETIC & (MIX)'),('one','Song','SONG');
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes,is_active)
              SELECT 'one','SP',column1,12,1000,1 FROM (VALUES ('H'),('A'),('L'));
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes,is_active)
              SELECT 'synth','SP',column1,11,1234,0 FROM (VALUES ('H'),('A'),('L'));
            UPDATE songs SET is_active=0 WHERE tag='synth';
            """);
    }

    private static string Row(string title = "Song", string difficulty = "A") => JsonSerializer.Serialize(new
    {
        name = title, difficulty, version = 30, d_value = difficulty == "H" ? 1 : difficulty == "L" ? 3 : 2,
        normal = "", hard = "地力S", n_value = 0, h_value = 9
    });
    private static DifficultyParseResult Parse(string text, DifficultyTableKind kind = DifficultyTableKind.Sp12Normal)
        => new DifficultyTableParser().Parse(kind, DifficultySource.FromUtf8(DifficultyTableDefinition.Get(kind).Url, Encoding.UTF8.GetBytes(text)));

    [Theory]
    [InlineData(DifficultyTableKind.Sp11Normal)]
    [InlineData(DifficultyTableKind.Sp11Hard)]
    [InlineData(DifficultyTableKind.Sp12Normal)]
    [InlineData(DifficultyTableKind.Sp12Hard)]
    public void FourTablesPassSourceAndChartConditionsWithoutGaugeConfusion(DifficultyTableKind kind)
    {
        // 非アクティブは除外せず、結果に活動状態を残す。通常DBのどの行も変更しない。
        var before = File.ReadAllBytes(Database.DatabasePath);
        foreach (var difficulty in new[] { "H", "A", "L" })
        {
            var input = Parse(DifficultyTableDefinition.Get(kind).Level == 11
                ? DifficultyProviderTests.Wiki(kind, difficulty) : "[" + Row(difficulty: difficulty) + "]", kind);
            var result = Service.Resolve(input);
            Assert.True(result.CanPrepareUpdate);
            var row = Assert.Single(result.Rows);
            Assert.NotNull(row.ChartId);
            Assert.Equal(difficulty, row.Resolution!.Request.Difficulty);
            Assert.Equal("SP", row.Resolution.Request.PlayStyle);
            Assert.Equal(input.Table.SourceName, row.Resolution.Request.SourceName);
            Assert.Equal(input.Table.Level, row.Resolution.Request.Level);
            var candidate = Assert.Single(row.Resolution.Candidates);
            Assert.Equal(input.Table.Level == 12, candidate.ChartIsActive);
            Assert.Equal(input.Table.Level == 12, candidate.SongIsActive);
        }
        Assert.Equal(before, File.ReadAllBytes(Database.DatabasePath));
    }

    [Theory]
    [InlineData("UPDATE charts SET level=11 WHERE tag='one';", "LEVEL_MISMATCH")]
    [InlineData("DELETE FROM charts WHERE tag='one' AND difficulty='A';", "CHART_NOT_FOUND")]
    [InlineData("UPDATE songs SET title='Other' WHERE tag='one';", "SONG_NOT_FOUND")]
    [InlineData("INSERT INTO songs(tag,title,normalized_title) VALUES('two','Ｓｏｎｇ','SONG');", "AMBIGUOUS_SONG")]
    [InlineData("INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES('synth','Song','SONG','manual');", "AMBIGUOUS_SONG")]
    public void UnresolvedReasonsArePreservedWithoutWeakeningMatching(string sql, string code)
    {
        Execute(sql);
        var result = Service.Resolve(Parse("[" + Row() + "]"));
        Assert.True(result.CanPrepareUpdate); // 全未解決も後続側でPARTIALとして受理できる。
        var row = Assert.Single(result.Rows);
        Assert.Equal(DifficultyRowStatus.Unresolved, row.Status);
        Assert.Null(row.ChartId);
        Assert.Contains(row.Diagnostics, d => d.Code == code);
    }

    [Theory]
    [InlineData("UPDATE charts SET total_notes=999 WHERE tag='synth';", "NOTES_MISMATCH")]
    [InlineData("UPDATE songs SET title='Other' WHERE tag='synth';", "TAG_TITLE_MISMATCH")]
    [InlineData("DELETE FROM charts WHERE tag='synth'; DELETE FROM songs WHERE tag='synth';", "TAG_NOT_FOUND")]
    public void WikiTagAndNotesAreChecked(string sql, string code)
    {
        Execute(sql);
        var result = Service.Resolve(Parse(DifficultyProviderTests.Wiki(DifficultyTableKind.Sp11Normal), DifficultyTableKind.Sp11Normal));
        Assert.Contains(Assert.Single(result.Rows).Diagnostics, d => d.Code == code);
    }

    [Fact]
    public void UnknownNotesAndSourceScopedAliasAreSupported()
    {
        Execute("""
            INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES
              ('one','Alias','ALIAS','IIDX_SP12_GITHUB'),('synth','Alias','ALIAS','unrelated');
            """);
        var result = Service.Resolve(Parse("[" + Row("Alias") + "]"));
        var row = Assert.Single(result.Rows);
        Assert.NotNull(row.ChartId);
        Assert.Null(row.Resolution!.Request.TotalNotes);
        Assert.Equal("one", Assert.Single(row.Resolution.Candidates).Tag);
    }

    [Theory]
    [InlineData("Song", "Song", "DUPLICATE_SOURCE_KEY")]
    [InlineData("Song", "Ｓｏｎｇ", "DUPLICATE_CHART")]
    [InlineData("Song", "Alias", "DUPLICATE_CHART")]
    public void AllDuplicateParticipantsAreConflicts(string first, string second, string code)
    {
        Execute("INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES ('one','Alias','ALIAS','manual');");
        var result = Service.Resolve(Parse("[" + Row(first) + "," + Row(second) + "]"));
        Assert.False(result.CanPrepareUpdate);
        Assert.Equal(2, result.Count(DifficultyRowStatus.Conflict));
        Assert.All(result.Rows, r => { Assert.Null(r.ChartId); Assert.Contains(r.Diagnostics, d => d.Code == code); });
    }

    [Fact]
    public void SourceDuplicatesAlsoConflictWithThirdAliasRow()
    {
        var result = Service.Resolve(Parse("[" + Row() + "," + Row() + "," + Row("Ｓｏｎｇ") + "]"));
        Assert.Equal(3, result.Count(DifficultyRowStatus.Conflict));
        using var connection = Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        long run = AddRun(connection, transaction, result);
        DifficultyMatchingAudit.SaveDiagnostics(connection, transaction, run, null, result);
        Assert.Equal(3L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports WHERE reason_code IN ('DUPLICATE_SOURCE_KEY','DUPLICATE_CHART');"));
    }

    [Fact]
    public void InvalidAndIncompleteSourcesKeepRowsAndPageDiagnostics()
    {
        var result = Service.Resolve(Parse("[" + Row() + "," + Row("Bad", "DP") + "," + Row("Missing") + "]"));
        Assert.False(result.CanPrepareUpdate);
        Assert.Equal(1, result.Count(DifficultyRowStatus.Invalid));
        Assert.Equal(1, result.Count(DifficultyRowStatus.Resolved));
        Assert.Equal(1, result.Count(DifficultyRowStatus.Unresolved));
        using var connection = Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        long run = AddRun(connection, transaction, result);
        DifficultyMatchingAudit.SaveDiagnostics(connection, transaction, run, null, result);
        Assert.Equal(2L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports;"));
        Assert.Equal(2L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports WHERE json_extract(raw_data,'$.row.raw') IS NOT NULL;"));
        var incomplete = DifficultyMatchingService.Resolve(Parse(DifficultyProviderTests.Wiki(DifficultyTableKind.Sp11Normal).Replace("地力F (0曲)", "不明 (0曲)"), DifficultyTableKind.Sp11Normal), connection, transaction);
        Assert.False(incomplete.CanPrepareUpdate);
        Assert.Equal(1, incomplete.Count(DifficultyRowStatus.NotProcessed));
        var sourceRun = AddRun(connection, transaction, incomplete);
        DifficultyMatchingAudit.SaveDiagnostics(connection, transaction, sourceRun, null, incomplete);
        Assert.True((long)Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports WHERE entity_type='DIFFICULTY_TABLE_SOURCE';") > 0);
        transaction.Rollback();
        Assert.Equal(0L, ReadScalar("SELECT count(*) FROM unresolved_imports;"));
    }

    [Fact]
    public void CurrentSnapshotReprocessesWholeTableAndResolvesOnlyAppliedCurrentPending()
    {
        var input = Parse("[" + Row("Alias") + "]");
        var initial = Service.Resolve(input);
        using var connection = Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        long run = AddRun(connection, transaction, initial, accepted: true);
        DifficultyMatchingAudit.SaveDiagnostics(connection, transaction, run, run, initial);
        // 同じ元行でも過去世代と未受理試行は現在世代の成功で解決しない。
        DifficultyMatchingAudit.SaveDiagnostics(connection, transaction, run, null, initial);
        Execute(connection, transaction, "INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES('one','Alias','ALIAS','manual');");
        var retry = DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, run, run);
        Assert.NotNull(Assert.Single(retry.Rows).ChartId);
        DifficultyMatchingAudit.ResolvePendingAfterApply(connection, transaction, TableCode, run, run);
        Assert.Equal(0L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports WHERE status='RESOLVED';"));
        // 表UPSERTは5-5担当。ここではその確定予定行を合成し、同一transactionの連動だけ検証する。
        SeedEntry(connection, transaction, retry.Rows[0].ChartId!.Value, run, "Alias");
        DifficultyMatchingAudit.ResolvePendingAfterApply(connection, transaction, TableCode, run, run);
        DifficultyMatchingAudit.ResolvePendingAfterApply(connection, transaction, TableCode, run, run);
        Assert.Equal(1L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports WHERE status='RESOLVED';"));
        Assert.Equal(1L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports WHERE status='PENDING';"));
        transaction.Rollback();
        Assert.Equal(0L, ReadScalar("SELECT count(*) FROM difficulty_table_entries;"));
    }

    [Fact]
    public void RevisedInputRejectsOldRunAndGenerationIncludingAtoBtoA()
    {
        using var connection = Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var a = DifficultyMatchingService.Resolve(Parse("[" + Row("Missing") + "]"), connection, transaction);
        var b = DifficultyMatchingService.Resolve(Parse("[" + Row() + "]"), connection, transaction);
        long first = AddRun(connection, transaction, a, accepted: true);
        DifficultyMatchingAudit.SaveDiagnostics(connection, transaction, first, first, a);
        long second = AddRun(connection, transaction, b, accepted: true, parent: first);
        Assert.Throws<InvalidOperationException>(() => DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, first, first));
        long third = AddRun(connection, transaction, a, accepted: true, parent: second);
        Assert.Throws<InvalidOperationException>(() => DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, third, first));
        Assert.Equal(1, DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, third, third).Count(DifficultyRowStatus.Unresolved));
        Assert.Equal(1L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports WHERE status='PENDING';"));
        long fourth = AddRun(connection, transaction, a, accepted: true, generation: third, parent: second);
        Assert.Throws<InvalidOperationException>(() => DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, third, third));
        Assert.NotNull(DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, fourth, third));
        // A再登場後にaliasを直しても、最初のA世代のPENDINGは旧版のまま保存する。
        Execute(connection, transaction, "INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES('one','Missing','MISSING','manual');");
        var current = DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, fourth, third);
        SeedEntry(connection, transaction, current.Rows[0].ChartId!.Value, fourth, "Missing");
        DifficultyMatchingAudit.ResolvePendingAfterApply(connection, transaction, TableCode, fourth, third);
        Assert.Equal(1L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports WHERE status='PENDING';"));
    }

    [Fact]
    public void HeldAndFailedRunsDoNotAdvanceAcceptedStateAndMissingSnapshotCannotBeSaved()
    {
        using var connection = Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var a = DifficultyMatchingService.Resolve(Parse("[" + Row("Missing") + "]"), connection, transaction);
        long first = AddRun(connection, transaction, a, accepted: true);
        var b = DifficultyMatchingService.Resolve(Parse("[" + Row() + "]"), connection, transaction);
        long held = AddRun(connection, transaction, b);
        long failed = AddRun(connection, transaction, b);
        Execute(connection, transaction, $"UPDATE import_runs SET status='FAILED' WHERE import_run_id={failed};");
        Assert.Equal(1, DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, first, first).Count(DifficultyRowStatus.Unresolved));
        Assert.Throws<InvalidOperationException>(() => DifficultyMatchingAudit.ResolvePendingAfterApply(connection, transaction, TableCode, held, first));
        Execute(connection, transaction, $"UPDATE import_runs SET options_json=NULL WHERE import_run_id={held};");
        Assert.Throws<InvalidOperationException>(() => DifficultyMatchingAudit.SaveDiagnostics(connection, transaction, held, null, b));
        Assert.Equal(0L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports;"));
    }

    [Fact]
    public void AliasRepairThatIntroducesCollisionHoldsWholeReplay()
    {
        using var connection = Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var input = DifficultyMatchingService.Resolve(Parse("[" + Row() + "," + Row("Alias") + "]"), connection, transaction);
        long run = AddRun(connection, transaction, input, accepted: true);
        DifficultyMatchingAudit.SaveDiagnostics(connection, transaction, run, run, input);
        Execute(connection, transaction, "INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES('one','Alias','ALIAS','manual');");
        Assert.False(DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, run, run).CanPrepareUpdate);
        Assert.Throws<InvalidOperationException>(() => DifficultyMatchingAudit.ResolvePendingAfterApply(connection, transaction, TableCode, run, run));
        Assert.Equal(1L, Scalar(connection, transaction, "SELECT count(*) FROM unresolved_imports WHERE status='PENDING';"));
    }

    [Theory]
    [InlineData("$.difficultyState.version", "2")]
    [InlineData("$.difficultyState.parentGenerationId", "100")]
    [InlineData("$.snapshot.parserVersion", "99")]
    [InlineData("$.difficultyState.inputSha256", "'wrong'")]
    public void CorruptOrUnknownAcceptedStateFailsClosed(string path, string value)
    {
        using var connection = Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var result = DifficultyMatchingService.Resolve(Parse("[" + Row() + "]"), connection, transaction);
        long run = AddRun(connection, transaction, result, accepted: true);
        Execute(connection, transaction, $"UPDATE import_runs SET options_json=json_set(options_json,'{path}',{value});");
        Assert.Throws<InvalidDataException>(() => DifficultyMatchingAudit.ReprocessCurrent(connection, transaction, TableCode, run, run));
    }

    [Fact]
    public async Task CancelledAndTamperedCandidatesCannotBecomeSuccessfulMatching()
    {
        var input = Parse("[" + Row("Missing") + "]");
        var fake = input with { Rows = [input.Rows[0] with { Candidate = new("Song", "one", "A", null, "UNRATED") }] };
        Assert.Equal(DifficultyRowStatus.Unresolved, Assert.Single(Service.Resolve(fake).Rows).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ResolveAsync(input, cancellation.Token));
    }

    private static long AddRun(SqliteConnection connection, SqliteTransaction transaction, DifficultyMatchingResult result,
        bool accepted = false, long? parent = null, long? generation = null)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "INSERT INTO import_runs(source_type,source_name,status) VALUES('DIFFICULTY_TABLE',$table,'PARTIAL'); SELECT last_insert_rowid();";
        query.Parameters.AddWithValue("$table", result.Input.Table.Code);
        long run = (long)query.ExecuteScalar()!;
        query.CommandText = "UPDATE import_runs SET options_json=$json WHERE import_run_id=$run;";
        query.Parameters.AddWithValue("$run", run);
        query.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new
        {
            stateCommitted = accepted, applyStatus = accepted ? "APPLIED" : "HELD",
            difficultyState = new { version = 1, tableCode = result.Input.Table.Code, acceptedGenerationId = generation ?? run,
                parentGenerationId = parent, inputSha256 = result.Input.Source!.Sha256 },
            snapshot = JsonSerializer.Deserialize<JsonElement>(DifficultyMatchingAudit.CreateSnapshot(result))
        }));
        query.ExecuteNonQuery();
        return run;
    }

    private static void SeedEntry(SqliteConnection connection, SqliteTransaction transaction, long chart, long run, string title)
    {
        Execute(connection, transaction, """
            INSERT INTO difficulty_tables(table_id,table_code,display_name,level,play_style,gauge_type,source_name)
              VALUES(1,'IIDX_SP12_GITHUB_SP12_NORMAL','Test',12,'SP','NORMAL','IIDX_SP12_GITHUB');
            INSERT INTO difficulty_ranks VALUES(1,'UNRATED','未割当','UNDECIDED',-20);
            """);
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "INSERT INTO difficulty_table_entries(table_id,chart_id,rank_code,source_title,source_difficulty,import_run_id) VALUES(1,$chart,'UNRATED',$title,'A',$run);";
        query.Parameters.AddWithValue("$chart", chart);
        query.Parameters.AddWithValue("$run", run);
        query.Parameters.AddWithValue("$title", title);
        query.ExecuteNonQuery();
    }
    private void Execute(string sql)
    {
        using var connection = Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, sql);
        transaction.Commit();
    }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction; query.CommandText = sql; query.ExecuteNonQuery();
    }
    private object ReadScalar(string sql)
    {
        using var connection = Database.OpenConnection(); using var transaction = connection.BeginTransaction();
        return Scalar(connection, transaction, sql);
    }
    private static object Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction; query.CommandText = sql; return query.ExecuteScalar()!;
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}

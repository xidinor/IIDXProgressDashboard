using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Difficulty;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests.Difficulty;

/// <summary>合成原本と独立DBで、公開更新APIの結果・監査・他データの保全を検証する。</summary>
public sealed class DifficultyUpdateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private DatabaseInitializer Database => new(Path.Combine(directory, "output.db"));
    private DifficultyUpdateService Service => new(Database, Path.Combine(directory, "backups"));
    private const string Code = "IIDX_SP12_GITHUB_SP12_NORMAL";

    public DifficultyUpdateTests()
    {
        Database.Initialize();
        Execute("""
            INSERT INTO songs(tag,title,normalized_title) VALUES('one','Song','SONG'),('two','Other','OTHER'),('synth','Synthetic & (mix)','SYNTHETIC & (MIX)');
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes) SELECT 'one','SP',column1,12,1000 FROM (VALUES('H'),('A'),('L'));
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes) VALUES('two','SP','A',12,1000);
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes,is_active) SELECT 'synth','SP',column1,11,1234,0 FROM (VALUES('H'),('A'),('L'));
            UPDATE songs SET is_active=0 WHERE tag='synth';
            INSERT INTO import_runs(source_type,source_name,status) VALUES('TEST','fixture','SUCCESS');
            INSERT INTO play_history(chart_id,played_at,clear_lamp,score,miss_count,source_system,source_record_key,import_run_id)
              VALUES(1,'2026-01-01T00:00:00Z',0,100,NULL,'TEST','one',1);
            INSERT INTO song_aliases(tag,alias_title,normalized_alias) VALUES('one','Alias','ALIAS');
            """);
    }

    private static string Row(string name = "Song", string difficulty = "A", string normal = "", int value = 0) =>
        JsonSerializer.Serialize(new { name, difficulty, version = 30, d_value = difficulty == "H" ? 1 : difficulty == "L" ? 3 : 2,
            normal, hard = "地力S", n_value = value, h_value = 9 });
    private static DifficultyParseResult Parse(string text, DifficultyTableKind kind = DifficultyTableKind.Sp12Normal) =>
        new DifficultyTableParser().Parse(kind, DifficultySource.FromUtf8(DifficultyTableDefinition.Get(kind).Url, Encoding.UTF8.GetBytes(text)));
    private Task<DifficultyUpdatePlan> Plan(params string[] rows) => Service.PrepareAsync(Parse("[" + string.Join(',', rows) + "]"));

    [Theory]
    [InlineData(DifficultyTableKind.Sp11Normal)]
    [InlineData(DifficultyTableKind.Sp11Hard)]
    [InlineData(DifficultyTableKind.Sp12Normal)]
    [InlineData(DifficultyTableKind.Sp12Hard)]
    public async Task FourTablesApplyAndRepeatWithoutChangingEntryProvenance(DifficultyTableKind kind)
    {
        var immutable = Dump("songs", "charts", "song_aliases", "play_history");
        var input = Parse(DifficultyTableDefinition.Get(kind).Level == 11 ? DifficultyProviderTests.Wiki(kind) : "[" + Row() + "]", kind);
        var first = await Service.ApplyAsync(await Service.PrepareAsync(input));
        Assert.Equal("SUCCESS", first.Status);
        Assert.Equal(new(1, 0, 0), first.Counts);
        var entries = Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries");
        var second = await Service.ApplyAsync(await Service.PrepareAsync(input));
        Assert.Equal(new(0, 0, 1), second.Counts);
        Assert.Equal(entries, Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries"));
        Assert.Equal(immutable, Dump("songs", "charts", "song_aliases", "play_history"));
        Assert.Equal(first.ImportRunId, Scalar("SELECT import_run_id FROM difficulty_table_entries;"));
        Assert.Equal(first.ImportRunId, Options(second.ImportRunId).GetProperty("difficultyState").GetProperty("acceptedGenerationId").GetInt64());
        Assert.Null(Scalar("PRAGMA foreign_key_check;"));
        Assert.Equal(1L, Scalar("PRAGMA foreign_keys;"));
    }

    [Fact]
    public async Task NormalHardAndHalRanksAreIndependentAndMoveWithStableIds()
    {
        var input = "[" + string.Join(',', new[] { Row(difficulty: "H"), Row(), Row(difficulty: "L") }) + "]";
        await Service.ApplyAsync(await Service.PrepareAsync(Parse(input)));
        var ids = Scalar("SELECT group_concat(table_id || ':' || chart_id) FROM difficulty_table_entries;");
        var hard = await Service.ApplyAsync(await Service.PrepareAsync(Parse(input, DifficultyTableKind.Sp12Hard)));
        Assert.Equal(3, hard.Counts.Added);
        Assert.Equal(3L, Scalar("SELECT count(*) FROM difficulty_table_entries WHERE rank_code='JIRIKI_S';"));
        var changed = input.Replace("\"n_value\":0", "\"n_value\":9").Replace("\"normal\":\"\"", "\"normal\":\"地力S\"");
        var update = await Service.ApplyAsync(await Service.PrepareAsync(Parse(changed)));
        Assert.Equal(new(0, 3, 0), update.Counts);
        Assert.Equal(ids, Scalar("SELECT group_concat(table_id || ':' || chart_id) FROM difficulty_table_entries WHERE table_id=1;"));
        Assert.Equal(42L, Scalar("SELECT count(*) FROM difficulty_ranks;"));
        Assert.Null(Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task MissingNeedsExactPlanConfirmationRetainsRowsAndReappearanceClearsRetention()
    {
        await Service.ApplyAsync(await Plan(Row(), Row("Other")));
        var before = Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries");
        var missing = await Plan(Row(normal: "地力S", value: 9));
        Assert.Equal("Other", Assert.Single(missing.MissingRows).Title);
        var held = await Service.ApplyAsync(missing);
        Assert.Equal("HELD", held.ApplyStatus);
        Assert.Equal("PARTIAL", held.Status);
        Assert.Equal(new(0, 0, 0), held.Counts);
        Assert.Equal(before, Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ApplyAsync(missing, Guid.NewGuid()));
        // HELDは受理基準を進めないため、提示済みの同じ差分を確認して適用できる。
        var applied = await Service.ApplyAsync(missing, missing.PlanId);
        Assert.Equal("PARTIAL", applied.Status);
        Assert.Equal(1, applied.Retained);
        Assert.Equal(2L, Scalar("SELECT count(*) FROM difficulty_table_entries;"));
        var repeated = await Service.ApplyAsync(await Plan(Row(normal: "地力S", value: 9)));
        Assert.Equal("PARTIAL", repeated.Status);
        Assert.Equal(1, repeated.Retained);
        Assert.Single(Options(repeated.ImportRunId).GetProperty("difficultyState").GetProperty("missingSourceKeys").EnumerateArray());
        var returned = await Service.ApplyAsync(await Plan(Row(normal: "地力S", value: 9), Row("Other")));
        Assert.Equal("SUCCESS", returned.Status);
        Assert.Equal(0, returned.Retained);
        Assert.Equal(new(0, 0, 2), returned.Counts);
    }

    [Fact]
    public async Task AllUnresolvedCanBeAcceptedAndCurrentReplayResolvesOnlyCurrentGeneration()
    {
        var first = await Service.ApplyAsync(await Plan(Row("Unknown")));
        Assert.Equal("PARTIAL", first.Status);
        Assert.Equal("APPLIED", first.ApplyStatus);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM difficulty_table_entries;"));
        Execute("INSERT INTO song_aliases(tag,alias_title,normalized_alias) VALUES('one','Unknown','UNKNOWN');");
        var retry = await Service.PrepareCurrentAsync(DifficultyTableKind.Sp12Normal, first.ImportRunId, first.ImportRunId);
        var applied = await Service.ApplyAsync(retry);
        Assert.Equal("SUCCESS", applied.Status);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM unresolved_imports WHERE status='RESOLVED';"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.PrepareCurrentAsync(DifficultyTableKind.Sp12Normal, first.ImportRunId, first.ImportRunId));
        Assert.Equal(first.ImportRunId, Options(applied.ImportRunId).GetProperty("difficultyState").GetProperty("acceptedGenerationId").GetInt64());
    }

    [Fact]
    public async Task AtoBtoAUsesNewGenerationAndDoesNotResolveOldPending()
    {
        var first = await Service.ApplyAsync(await Plan(Row("Unknown")));
        var b = await Plan(Row("Other"));
        await Service.ApplyAsync(b, b.PlanId);
        Execute("INSERT INTO song_aliases(tag,alias_title,normalized_alias) VALUES('one','Unknown','UNKNOWN');");
        var a = await Plan(Row("Unknown"));
        var applied = await Service.ApplyAsync(a, a.PlanId);
        Assert.Equal(applied.ImportRunId, Options(applied.ImportRunId).GetProperty("difficultyState").GetProperty("acceptedGenerationId").GetInt64());
        Assert.Equal(1L, Scalar("SELECT count(*) FROM unresolved_imports WHERE status='PENDING';"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.PrepareCurrentAsync(DifficultyTableKind.Sp12Normal, applied.ImportRunId, first.ImportRunId));
    }

    [Fact]
    public async Task NewlyUnresolvedOldEntryIsRetainedWithoutInventedDeletion()
    {
        await Service.ApplyAsync(await Plan(Row(), Row("Other")));
        Execute("UPDATE charts SET level=11 WHERE tag='two';");
        var result = await Service.ApplyAsync(await Plan(Row(), Row("Other")));
        Assert.Equal("PARTIAL", result.Status);
        Assert.Equal(1, result.Unresolved);
        Assert.Equal(1, result.Retained);
        Assert.Equal(0, result.Missing);
        Assert.Equal(2L, Scalar("SELECT count(*) FROM difficulty_table_entries;"));
        Assert.Equal("LEVEL_MISMATCH", Scalar("SELECT reason_code FROM unresolved_imports;"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{broken")]
    [InlineData("duplicate")]
    [InlineData("collision")]
    [InlineData("invalid")]
    public async Task BadInputsPreserveExistingTableAndSaveOriginalAndDiagnostics(string variant)
    {
        await Service.ApplyAsync(await Plan(Row()));
        var before = Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries");
        string text = variant switch
        {
            "duplicate" => "[" + Row() + "," + Row() + "]",
            "collision" => "[" + Row() + "," + Row("Alias") + "]",
            "invalid" => "[" + Row(normal: "unknown") + "]",
            _ => variant
        };
        var result = await Service.ApplyAsync(await Service.PrepareAsync(Parse(text)));
        Assert.Equal("FAILED", result.Status);
        Assert.Equal(new(0, 0, 0), result.Counts);
        Assert.Equal(before, Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries"));
        Assert.Equal(text, Options(result.ImportRunId).GetProperty("snapshot").GetProperty("input").GetProperty("source").GetProperty("content").GetString());
        Assert.True(Convert.ToInt64(Scalar("SELECT count(*) FROM unresolved_imports;")) > 0);
        Assert.False(Options(result.ImportRunId).GetProperty("stateCommitted").GetBoolean());
    }

    [Theory]
    [InlineData("UPDATE songs SET artist='changed' WHERE tag='one';")]
    [InlineData("UPDATE charts SET total_notes=999 WHERE tag='one';")]
    [InlineData("UPDATE song_aliases SET note='changed';")]
    public async Task StaleMatchingAssumptionsRejectPlanAndRecordFailure(string sql)
    {
        var plan = await Plan(Row());
        Execute(sql);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ApplyAsync(plan));
        Assert.NotNull(error.Data["DifficultyImportRunId"]);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM difficulty_tables;"));
        Assert.Equal("FAILED", Scalar("SELECT status FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
    }

    [Fact]
    public async Task AnotherAcceptedRunInvalidatesPlanEvenWhenInputAndEntriesAreUnchanged()
    {
        await Service.ApplyAsync(await Plan(Row()));
        var old = await Plan(Row());
        await Service.ApplyAsync(await Plan(Row()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ApplyAsync(old));
    }

    [Theory]
    [InlineData("UPDATE difficulty_table_entries SET source_title='manual';")]
    [InlineData("UPDATE difficulty_tables SET gauge_type='HARD';")]
    [InlineData("UPDATE difficulty_ranks SET sort_order=999;")]
    [InlineData("DELETE FROM difficulty_table_entries;")]
    [InlineData("INSERT INTO difficulty_table_entries(table_id,chart_id,rank_code) VALUES(1,1,'UNRATED');")]
    [InlineData("UPDATE import_runs SET options_json=json_set(options_json,'$.difficultyState.version',99) WHERE source_type='DIFFICULTY_TABLE';")]
    [InlineData("UPDATE import_runs SET options_json='broken' WHERE source_type='DIFFICULTY_TABLE';")]
    public async Task UnknownOrExternallyModifiedOwnershipIsNeverAdopted(string sql)
    {
        await Service.ApplyAsync(await Plan(Row()));
        Execute(sql);
        var before = File.ReadAllBytes(Database.DatabasePath);
        await Assert.ThrowsAnyAsync<Exception>(() => Plan(Row()));
        Assert.Equal(before, File.ReadAllBytes(Database.DatabasePath));
    }

    [Fact]
    public async Task UnownedExistingTableAndRunningRunFailClosed()
    {
        Execute($"INSERT INTO difficulty_tables(table_code,display_name,level,play_style,gauge_type,source_name) VALUES('{Code}','manual',12,'SP','NORMAL','IIDX_SP12_GITHUB');");
        await Assert.ThrowsAsync<InvalidDataException>(() => Plan(Row()));
        Execute("DELETE FROM difficulty_tables;");
        var plan = await Plan(Row());
        Execute($"INSERT INTO import_runs(source_type,source_name,status,options_json) VALUES('DIFFICULTY_TABLE','{Code}','RUNNING','{{\"stateCommitted\":false}}');");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Plan(Row()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ApplyAsync(plan));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM import_runs WHERE status='RUNNING';"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM difficulty_tables;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MidWriteFailureOrCancellationRollsBackEntriesStateAndPending(bool cancel)
    {
        var first = await Service.ApplyAsync(await Plan(Row("Unknown"), Row("Other")));
        Execute("INSERT INTO song_aliases(tag,alias_title,normalized_alias) VALUES('one','Unknown','UNKNOWN');");
        var plan = await Service.PrepareCurrentAsync(DifficultyTableKind.Sp12Normal, first.ImportRunId, first.ImportRunId);
        var before = Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries", "unresolved_imports");
        using var cancellation = new CancellationTokenSource();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Service.ApplyAsync(plan, cancellationToken: cancellation.Token,
            progress: new InlineProgress(_ => { if (cancel) cancellation.Cancel(); else throw new IOException("injected failure"); })));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(before, Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries", "unresolved_imports"));
        long run = Convert.ToInt64(error.Data["DifficultyImportRunId"]);
        Assert.Equal("FAILED", Scalar($"SELECT status FROM import_runs WHERE import_run_id={run};"));
        Assert.Equal(0L, Scalar($"SELECT records_imported FROM import_runs WHERE import_run_id={run};"));
        Assert.False(Options(run).GetProperty("stateCommitted").GetBoolean());
        // 失敗後も直前の受理世代から再準備できる。
        Assert.NotNull(await Service.PrepareCurrentAsync(DifficultyTableKind.Sp12Normal, first.ImportRunId, first.ImportRunId));
    }

    [Fact]
    public async Task CancelledBeforeApplyStillRecordsFailureButPreparationDoesNotWrite()
    {
        var plan = await Plan(Row("Unknown"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.PrepareAsync(plan.Input, cancellationToken: cancellation.Token));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM import_runs;"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ApplyAsync(plan, cancellationToken: cancellation.Token));
        Assert.Equal("FAILED", Scalar("SELECT status FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM unresolved_imports WHERE status='PENDING';"));
    }

    [Fact]
    public async Task BackupFailureDoesNotStartRunAndWalBackupCanBeRestoredSeparately()
    {
        var plan = await Plan(Row());
        string blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "file, not directory");
        await Assert.ThrowsAnyAsync<IOException>(() => new DifficultyUpdateService(Database, blocked).ApplyAsync(plan));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM import_runs;"));
        using var keepWal = Database.OpenConnection();
        using var command = keepWal.CreateCommand(); command.CommandText = "PRAGMA journal_mode=WAL;"; command.ExecuteScalar();
        Execute("UPDATE songs SET artist='in WAL' WHERE tag='one';");
        var applied = await Service.ApplyAsync(await Plan(Row()));
        string restoredPath = Path.Combine(directory, "restored.db"); File.Copy(applied.BackupPath, restoredPath);
        var restored = new DatabaseInitializer(restoredPath); restored.Initialize();
        using var restoredConnection = restored.OpenConnection();
        Assert.Equal("in WAL", Scalar(restoredConnection, "SELECT artist FROM songs WHERE tag='one';"));
        Assert.Equal(0L, Scalar(restoredConnection, "SELECT count(*) FROM difficulty_tables;"));
        Assert.Equal(1L, Scalar(restoredConnection, "SELECT count(*) FROM play_history;"));
        Assert.Null(Scalar(restoredConnection, "PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task AnotherTableIsPreservedAndDoesNotInvalidatePreparedPlan()
    {
        var normal = await Plan(Row());
        var hard = await Service.ApplyAsync(await Service.PrepareAsync(Parse("[" + Row() + "]", DifficultyTableKind.Sp12Hard)));
        await Service.ApplyAsync(normal);
        Assert.Equal(hard.ImportRunId, Scalar("SELECT e.import_run_id FROM difficulty_table_entries e JOIN difficulty_tables t USING(table_id) WHERE t.gauge_type='HARD';"));
        Assert.Equal(2L, Scalar("SELECT count(*) FROM difficulty_tables;"));
    }

    [Theory]
    [InlineData("BEFORE_ENTRIES")]
    [InlineData("BEFORE_FINISH")]
    public async Task SqlFailureDuringEntriesOrFinalLogRollsBackAndRecordsFailed(string stage)
    {
        // スキーマ検査後、同一接続の一時triggerで実SQLite例外を発生させる。
        // 2行目の失敗は1行目の書込み、終了ログ失敗は全エントリーを戻す必要がある。
        var service = new DifficultyUpdateService(Database, directory, (connection, transaction, point) =>
        {
            if (point != stage) return;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = point == "BEFORE_ENTRIES" ? """
                CREATE TEMP TRIGGER fail_entry BEFORE INSERT ON main.difficulty_table_entries
                WHEN NEW.source_title='Other' BEGIN SELECT RAISE(ABORT,'injected entry failure'); END;
                """ : """
                CREATE TEMP TRIGGER fail_finish BEFORE UPDATE ON main.import_runs
                WHEN NEW.status IN ('SUCCESS','PARTIAL') BEGIN SELECT RAISE(ABORT,'injected final log failure'); END;
                """;
            command.ExecuteNonQuery();
        });
        var plan = await Plan(Row(), Row("Other"));
        var error = await Assert.ThrowsAsync<SqliteException>(() => service.ApplyAsync(plan));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM difficulty_tables;"));
        Assert.Equal("FAILED", Scalar("SELECT status FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        Assert.Equal(0L, Scalar("SELECT records_imported FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        Assert.NotNull(error.Data["DifficultyBackupPath"]);
    }

    [Fact]
    public async Task FailureLogFailureReturnsBothErrorsAndLeavesRunningForRecovery()
    {
        var plan = await Plan(Row());
        var service = new DifficultyUpdateService(Database, directory, (connection, transaction, point) =>
        {
            if (point == "BEFORE_ENTRIES") throw new IOException("injected update failure");
            if (point != "BEFORE_FAILURE_LOG") return;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                CREATE TEMP TRIGGER fail_logging BEFORE UPDATE ON main.import_runs
                BEGIN SELECT RAISE(ABORT,'injected failure log failure'); END;
                """;
            command.ExecuteNonQuery();
        });
        var error = await Assert.ThrowsAsync<AggregateException>(() => service.ApplyAsync(plan));
        Assert.IsType<IOException>(error.InnerExceptions[0]);
        Assert.IsType<SqliteException>(error.InnerExceptions[1]);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM difficulty_tables;"));
        Assert.Equal("RUNNING", Scalar("SELECT status FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        Assert.Equal(DBNull.Value, Scalar("SELECT completed_at FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Plan(Row()));
    }

    [Fact]
    public async Task ConcurrentPlansCannotBothApplyTheSameBaseline()
    {
        var first = await Plan(Row());
        var second = await Plan(Row());
        async Task<bool> Apply(DifficultyUpdatePlan plan)
        {
            try { await Service.ApplyAsync(plan); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Apply(first), Apply(second));
        Assert.Single(results, success => success);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM difficulty_table_entries;"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM import_runs WHERE status='RUNNING';"));
    }

    [Fact]
    public async Task FailedAcquisitionAndDetachedPlanInputRemainAuditable()
    {
        var table = DifficultyTableDefinition.Get(DifficultyTableKind.Sp12Normal);
        var failed = new DifficultyParseResult(table, null, [], [new("HTTP_ERROR", "HTTP 403")], "FAILED", "NOT_RUN", false);
        var failure = await Service.ApplyAsync(await Service.PrepareAsync(failed));
        Assert.Equal("FAILED", failure.Status);
        Assert.Equal("DIFFICULTY_TABLE_SOURCE", Scalar("SELECT entity_type FROM unresolved_imports;"));
        Assert.Equal(0L, Scalar("SELECT records_unresolved FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        var plan = await Plan(Row());
        // UIへ渡す入力コピーの行配列を変更しても、保存された計画・確認対象は変化しない。
        var copy = plan.Input;
        if (copy.Rows is IList<DifficultySourceRow> { IsReadOnly: false } mutable) mutable.Clear();
        var result = await Service.ApplyAsync(plan);
        Assert.Equal("SUCCESS", result.Status);
        Assert.Equal(1, result.Counts.Added);
    }

    [Fact]
    public async Task UnknownDatabaseAndOtherDatabasePlanAreRejectedWithoutWriting()
    {
        var other = new DatabaseInitializer(Path.Combine(directory, "other.db")); other.Initialize();
        var plan = await Plan(Row());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DifficultyUpdateService(other, directory).ApplyAsync(plan));
        string unknownPath = Path.Combine(directory, "legacy.db");
        using (var connection = new SqliteConnection($"Data Source={unknownPath};Pooling=False"))
        { connection.Open(); using var query = connection.CreateCommand(); query.CommandText = "CREATE TABLE play_history(id INTEGER);"; query.ExecuteNonQuery(); }
        var before = File.ReadAllBytes(unknownPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DifficultyUpdateService(new(unknownPath), directory).PrepareAsync(Parse("[" + Row() + "]")));
        Assert.Equal(before, File.ReadAllBytes(unknownPath));
    }

    private JsonElement Options(long run)
    { using var doc = JsonDocument.Parse((string)Scalar($"SELECT options_json FROM import_runs WHERE import_run_id={run};")!); return doc.RootElement.Clone(); }
    private void Execute(string sql)
    { using var connection = Database.OpenConnection(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private object? Scalar(string sql) { using var connection = Database.OpenConnection(); return Scalar(connection, sql); }
    private static object? Scalar(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private string Dump(params string[] tables)
    {
        using var connection = Database.OpenConnection();
        var rows = new List<object?[]>();
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        }
        return JsonSerializer.Serialize(rows);
    }
    private sealed class InlineProgress(Action<int> action) : IProgress<int> { public void Report(int value) => action(value); }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}

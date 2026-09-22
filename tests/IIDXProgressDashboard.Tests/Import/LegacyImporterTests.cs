using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Import;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class LegacyImporterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid sourceId = Guid.NewGuid();
    private string Input => Path.Combine(directory, "legacy.db");
    private DatabaseInitializer Database => new(Path.Combine(directory, "output.db"));
    private LegacyInfinitasLogImporter Importer => new(Database, Path.Combine(directory, "backups"));

    public LegacyImporterTests()
    {
        Directory.CreateDirectory(directory);
        Database.Initialize();
        Source("""
            CREATE TABLE play_history(id INTEGER PRIMARY KEY,level TEXT,song_name TEXT,difficulty_type TEXT,
                total_notes INTEGER,clear_type TEXT,score INTEGER,miss_count INTEGER,played_option TEXT,played_at TEXT,original_data TEXT);
            """);
        Output("""
            INSERT INTO songs(tag,title,normalized_title) VALUES ('one','合成曲','合成曲');
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes)
            SELECT 'one',s.column1,d.column1,11,1000
            FROM (VALUES ('SP'),('DP')) s CROSS JOIN (VALUES ('B'),('N'),('H'),('A'),('L')) d;
            """);
    }

    private void Add(long id, string title = "合成曲", string difficulty = "SPA", string lamp = "CLEAR", long? bp = null,
        long score = 1500, string date = "2026-01-01-00-01") => Source("""
        INSERT INTO play_history VALUES ($id,'11',$title,$difficulty,1000,$lamp,$score,$bp,'OFF',$date,$raw);
        """, ("$id", id), ("$title", title), ("$difficulty", difficulty), ("$lamp", lamp), ("$score", score),
        ("$bp", bp), ("$date", date), ("$raw", "['合成曲', \"引用\", None]\n\\"));

    [Fact]
    public async Task ReimportAppendPastDatesAndWorsePlaysPreserveEverySourceRow()
    {
        // 内容一致・同分の別idも、悪化したプレイも個別の事実として保持する。
        Add(1, lamp: "NO PLAY"); Add(2, lamp: "NO PLAY"); Add(3, bp: 0, score: 1400);
        var original = SHA256.HashData(File.ReadAllBytes(Input));
        var first = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal((3, 3, 0, "SUCCESS"), (first.Read, first.Imported, first.Duplicates, first.Status));
        Assert.True(File.Exists(first.BackupPath));
        var again = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal((0, 3), (again.Imported, again.Duplicates));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(Input)));
        Add(4, date: "2025-01-01-00-00", score: 1300, bp: 50);
        Add(5, lamp: "NO PLAY");
        var appended = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal((2, 3), (appended.Imported, appended.Duplicates));
        Assert.Equal(5L, Count("play_history"));
        Assert.Equal(3L, Value("SELECT COUNT(*) FROM play_history WHERE clear_lamp=0 AND score>0"));
        Assert.Equal(3L, Value("SELECT COUNT(*) FROM play_history WHERE miss_count IS NULL"));
        Assert.Equal(1L, Value("SELECT COUNT(*) FROM play_history WHERE miss_count=0"));
        Assert.Equal("2025-12-31T15:01:00Z", Value("SELECT played_at FROM play_history WHERE score=1500 LIMIT 1"));
        Assert.Equal(5L, Value("SELECT COUNT(*) FROM play_history WHERE pgreat IS NULL AND play_side IS NULL"));
        using var raw = JsonDocument.Parse((string)Value("SELECT raw_data FROM play_history LIMIT 1")!);
        Assert.Equal("['合成曲', \"引用\", None]\n\\", raw.RootElement.GetProperty("row").GetProperty("original_data").GetString());
    }

    [Fact]
    public async Task SourceIdentitySurvivesCopyAndSeparatesDifferentDatabases()
    {
        Add(1);
        await Importer.ImportAsync(Input, sourceId);
        var copy = Path.Combine(directory, "renamed.db");
        File.Copy(Input, copy);
        var same = await Importer.ImportAsync(copy, sourceId);
        Assert.Equal((0, 1), (same.Imported, same.Duplicates));
        var different = await Importer.ImportAsync(copy, Guid.NewGuid());
        Assert.Equal(1, different.Imported);
        Assert.Equal(2L, Count("play_history"));
    }

    [Fact]
    public async Task ChangedDeletedAndReusedIdsNeverOverwriteOrDeleteHistory()
    {
        Add(1); Add(2);
        await Importer.ImportAsync(Input, sourceId);
        Source("UPDATE play_history SET score=1200 WHERE id=1; DELETE FROM play_history WHERE id=2;");
        var changed = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal((1, 0, "PARTIAL"), (changed.Conflicts, changed.Imported, changed.Status));
        Assert.Equal(2L, Value("SELECT COUNT(*) FROM play_history WHERE score=1500"));
        Add(2, score: 1100);
        Assert.Equal(2, (await Importer.ImportAsync(Input, sourceId)).Conflicts);
        Assert.Equal(2L, Count("play_history"));
        Assert.Equal(3L, Value("SELECT COUNT(*) FROM unresolved_imports WHERE reason_code='SOURCE_ROW_CHANGED'"));
    }

    [Fact]
    public async Task UnresolvedAttemptsAreAuditedThenResolvedExactlyOnce()
    {
        Add(1, title: "別名"); Add(2, lamp: "bad"); Add(3, difficulty: "SPX");
        var first = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal((1, 2, "PARTIAL"), (first.Unresolved, first.Invalid, first.Status));
        await Importer.ImportAsync(Input, sourceId);
        Assert.Equal(6L, Count("unresolved_imports"));
        Output("INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES ('one','別名','別名','manual');");
        var resolved = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal(1, resolved.Imported);
        Assert.Equal(2L, Value("SELECT COUNT(*) FROM unresolved_imports WHERE status='RESOLVED'"));
        Assert.Equal(1, (await Importer.ImportAsync(Input, sourceId)).Duplicates);
        Source("UPDATE play_history SET clear_type='CLEAR' WHERE id=2;");
        var correctedInput = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal(1, correctedInput.Conflicts);
        Assert.Equal(1L, Count("play_history"));
    }

    [Fact]
    public async Task IntegratedSourceTagsAreValidatedAndEmptyTagsUseTitle()
    {
        Add(1); Add(2); Add(3); Add(4);
        Source("ALTER TABLE play_history ADD COLUMN song_tag TEXT; UPDATE play_history SET song_tag=CASE id WHEN 1 THEN 'one' WHEN 2 THEN '' WHEN 3 THEN 'other' ELSE NULL END;");
        Output("INSERT INTO songs(tag,title,normalized_title) VALUES ('other','異なる曲','異なる曲');");
        var result = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal((3, 1), (result.Imported, result.Unresolved));
        Assert.Equal("TAG_TITLE_MISMATCH", Value("SELECT reason_code FROM unresolved_imports"));
    }

    [Fact]
    public async Task AllChartKindsLampsAndMismatchReasonsArePreserved()
    {
        var lamps = new[] { "NO PLAY", "FAILED", "A-CLEAR", "E-CLEAR", "CLEAR", "H-CLEAR", "EXH-CLEAR", "F-COMBO" };
        var id = 0;
        foreach (var style in new[] { "SP", "DP" })
            foreach (var difficulty in "BNHAL") Add(++id, difficulty: style + difficulty, lamp: lamps[(id - 1) % 8]);
        Add(11); Add(12); Add(13, title: "曖昧"); Add(14, difficulty: "SB");
        Source("UPDATE play_history SET level='12' WHERE id=11; UPDATE play_history SET total_notes=900 WHERE id=12;");
        Output("INSERT INTO songs(tag,title,normalized_title) VALUES ('a','曖昧','曖昧'),('b','曖昧','曖昧');");
        var result = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal((10, 3, 1), (result.Imported, result.Unresolved, result.Invalid));
        Assert.Equal(10L, Value("SELECT COUNT(DISTINCT chart_id) FROM play_history"));
        Assert.Equal(8L, Value("SELECT COUNT(DISTINCT clear_lamp) FROM play_history"));
        foreach (var code in new[] { "LEVEL_MISMATCH", "NOTES_MISMATCH", "AMBIGUOUS_SONG", "INVALID_DIFFICULTY" })
            Assert.Equal(1L, Value("SELECT COUNT(*) FROM unresolved_imports WHERE reason_code=$code", ("$code", code)));
    }

    [Fact]
    public async Task UnknownSchemaAndNewDatabaseAreRefusedAndLogged()
    {
        Source("ALTER TABLE play_history ADD COLUMN unknown TEXT;");
        await Assert.ThrowsAsync<InvalidDataException>(() => Importer.ImportAsync(Input, sourceId));
        Assert.Equal("FAILED", Value("SELECT status FROM import_runs"));
        Assert.Equal(0L, Count("play_history"));
        var other = new DatabaseInitializer(Path.Combine(directory, "new.db"));
        other.Initialize();
        await Assert.ThrowsAsync<InvalidDataException>(() => Importer.ImportAsync(other.DatabasePath, Guid.NewGuid()));
        Assert.Equal(2L, Value("SELECT COUNT(*) FROM import_runs WHERE status='FAILED'"));
    }

    [Fact]
    public async Task SamePathAndHardLinkAreRefusedBeforeAnyWrite()
    {
        var hash = SHA256.HashData(File.ReadAllBytes(Database.DatabasePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Importer.ImportAsync(Database.DatabasePath, sourceId));
        var link = Path.Combine(directory, "linked.db");
        Assert.True(CreateHardLink(link, Database.DatabasePath, IntPtr.Zero));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Importer.ImportAsync(link, sourceId));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Database.DatabasePath)));
    }

    [Fact]
    public async Task BackupFailureDoesNotEvenCreateRunningLog()
    {
        var file = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        var importer = new LegacyInfinitasLogImporter(Database, file);
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(Input, sourceId));
        Assert.Equal(0L, Count("import_runs"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndCancellationRollbackAllRowsAndKeepFailureLog(bool cancel)
    {
        Add(1); Add(2);
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(_ => { if (cancel) cts.Cancel(); else throw new IOException("合成書込途中障害"); });
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Importer.ImportAsync(Input, sourceId, cts.Token, progress));
        else await Assert.ThrowsAsync<IOException>(() => Importer.ImportAsync(Input, sourceId, cts.Token, progress));
        Assert.Equal(0L, Count("play_history"));
        Assert.Equal(0L, Count("unresolved_imports"));
        Assert.Equal("FAILED", Value("SELECT status FROM import_runs"));
        Assert.Equal(0L, Value("SELECT records_imported FROM import_runs"));
        Assert.Equal(2, (await Importer.ImportAsync(Input, sourceId)).Imported);
    }

    [Fact]
    public async Task ConcurrentImportsInsertEachRowOnce()
    {
        Add(1); Add(2);
        var results = await Task.WhenAll(Importer.ImportAsync(Input, sourceId), Importer.ImportAsync(Input, sourceId));
        Assert.Equal(2, results.Sum(r => r.Imported));
        Assert.Equal(2, results.Sum(r => r.Duplicates));
        Assert.Equal(2L, Count("play_history"));
        Assert.Equal(2L, Value("SELECT COUNT(*) FROM import_runs WHERE status='SUCCESS'"));
    }

    [Fact]
    public async Task CancelledResolutionRestoresPendingAttemptsAndBackupCanBeOpened()
    {
        Add(1, title: "後で解決");
        var pending = await Importer.ImportAsync(Input, sourceId);
        Output("INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES ('one','後で解決','後で解決','manual');");
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Importer.ImportAsync(Input, sourceId, cts.Token,
            new InlineProgress(_ => cts.Cancel())));
        Assert.Equal("PENDING", Value("SELECT status FROM unresolved_imports"));
        Assert.Equal(0L, Count("play_history"));
        Assert.Equal(1, (await Importer.ImportAsync(Input, sourceId)).Imported);
        // 復旧は接続を閉じてバックアップを別パスへコピーしてから検証する。運用DBを上書きしない。
        var restored = Path.Combine(directory, "restored.db");
        File.Copy(pending.BackupPath, restored);
        var restoredDatabase = new DatabaseInitializer(restored);
        restoredDatabase.Initialize();
        using var connection = restoredDatabase.OpenConnection();
        using var check = Create(connection, "SELECT COUNT(*) FROM charts;");
        Assert.Equal(10L, check.ExecuteScalar());
    }

    [Fact]
    public async Task InvalidStorageTypesAndMissingRequiredValuesKeepOriginalData()
    {
        Add(1); Add(2); Add(3); Add(4);
        Source("UPDATE play_history SET score=NULL WHERE id=1; UPDATE play_history SET score=1.5 WHERE id=2; UPDATE play_history SET played_option=X'0102' WHERE id=3; UPDATE play_history SET level=NULL,total_notes=NULL,original_data=NULL,played_option='' WHERE id=4;");
        var result = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal((1, 3), (result.Imported, result.Invalid));
        using var raw = JsonDocument.Parse((string)Value("SELECT raw_data FROM unresolved_imports WHERE reason_code='INVALID_PLAYED_OPTION'")!);
        Assert.Equal("blob", raw.RootElement.GetProperty("storageTypes").GetProperty("played_option").GetString());
        Assert.Equal(new byte[] { 1, 2 }, raw.RootElement.GetProperty("row").GetProperty("played_option").GetBytesFromBase64());
        Assert.Equal(1L, Value("SELECT COUNT(*) FROM play_history WHERE level_at_play IS NULL AND total_notes_at_play IS NULL"));
    }

    [Fact]
    public async Task EmptyInputPrecancelAndChangedTimestampHaveExplicitOutcomes()
    {
        var empty = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal((0, "SUCCESS"), (empty.Read, empty.Status));
        Add(1);
        await Importer.ImportAsync(Input, sourceId);
        Source("UPDATE play_history SET played_at='2026-01-01-09-01';");
        Assert.Equal(1, (await Importer.ImportAsync(Input, sourceId)).Conflicts);
        Assert.Equal("2025-12-31T15:01:00Z", Value("SELECT played_at FROM play_history"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Importer.ImportAsync(Input, sourceId, new CancellationToken(true)));
        Assert.Equal(1L, Value("SELECT COUNT(*) FROM import_runs WHERE status='FAILED' AND message LIKE 'CANCELLED:%'"));
        using var options = JsonDocument.Parse((string)Value("SELECT options_json FROM import_runs LIMIT 1")!);
        Assert.Equal("+09:00", options.RootElement.GetProperty("sourceTimeZone").GetString());
    }

    [Fact]
    public void PreferredCandidateIsIntegratedAndNeverConcatenates()
    {
        Assert.Null(LegacyInfinitasLogImporter.SelectPreferredSource(directory));
        var old = Path.Combine(directory, "infinitas_log.db");
        File.Copy(Input, old);
        Assert.Equal(old, LegacyInfinitasLogImporter.SelectPreferredSource(directory));
        var integrated = Path.Combine(directory, "iidx-progress.db");
        File.WriteAllText(integrated, "不正な候補でも黙ってfallbackしない");
        Assert.Equal(integrated, LegacyInfinitasLogImporter.SelectPreferredSource(directory));
    }

    [Theory]
    [InlineData(true, false, false, null)]
    [InlineData(true, true, false, null)]
    [InlineData(false, true, false, "EXTERNAL_ID_REVIEW_REQUIRED")]
    [InlineData(false, false, false, "SONG_NOT_FOUND")]
    [InlineData(true, true, true, "EXTERNAL_ID_CONFLICT")]
    public async Task ExternalMatchingUsesExistingPendingAndReimportFlow(bool primary, bool external, bool conflict, string? code)
    {
        Add(1);
        if (!primary) Output("UPDATE songs SET title='別表記' WHERE tag='one';");
        if (conflict) Output("INSERT INTO songs(tag,title,normalized_title) VALUES('two','Other','OTHER');");
        if (external) Output("INSERT INTO external_song_ids(external_song_id,title,normalized_title,tag) VALUES(1,'合成曲','合成曲','" + (conflict ? "two" : "one") + "');");
        var result = await Importer.ImportAsync(Input, sourceId);
        Assert.Equal(code is null ? 1 : 0, result.Imported);
        if (code is null) Assert.Equal(1, (await Importer.ImportAsync(Input, sourceId)).Duplicates);
        else
        {
            Assert.Equal(code, Value("SELECT reason_code FROM unresolved_imports;"));
            Assert.Equal("PENDING", Value("SELECT status FROM unresolved_imports;"));
            Assert.Equal(0L, Count("play_history"));
            if (external) Assert.Contains("IIDX_DATA_TABLE", (string)Value("SELECT reason_detail FROM unresolved_imports;")!);
            if (!primary && external)
            {
                // 手動alias登録後も、元行キーと元データを変えず再照合する。
                new SongAliasRepository(Database, Path.Combine(directory, "backups")).Register("one", "合成曲");
                Assert.Equal(1, (await Importer.ImportAsync(Input, sourceId)).Imported);
                Assert.Equal("RESOLVED", Value("SELECT status FROM unresolved_imports;"));
            }
        }
    }

    private sealed class InlineProgress(Action<int> action) : IProgress<int>
    { public void Report(int value) => action(value); }

    private long Count(string table) => (long)Value("SELECT COUNT(*) FROM " + table)!;
    private object? Value(string sql, params (string, object?)[] values)
    {
        using var c = Database.OpenConnection();
        using var command = Create(c, sql, values);
        return command.ExecuteScalar();
    }
    private void Source(string sql, params (string, object?)[] values)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Input, Pooling = false, ForeignKeys = true }.ToString());
        c.Open();
        using var command = Create(c, sql, values);
        command.ExecuteNonQuery();
    }
    private void Output(string sql)
    {
        using var c = Database.OpenConnection();
        using var command = Create(c, sql);
        command.ExecuteNonQuery();
    }
    private static SqliteCommand Create(SqliteConnection c, string sql, params (string, object?)[] values)
    {
        var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr security);

    public void Dispose() => Directory.Delete(directory, true);
}

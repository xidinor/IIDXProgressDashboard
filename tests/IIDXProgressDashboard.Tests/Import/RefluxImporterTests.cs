using System.Security.Cryptography;
using System.Text;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Import;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests.Import;

public sealed class RefluxImporterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();
    private string Input => Path.Combine(directory, "session.tsv");
    private DatabaseInitializer Database => new(Path.Combine(directory, "output.db"));
    private RefluxSessionTsvImporter Importer => new(Database, Path.Combine(directory, "backups"));
    private const string Header = "title\tdifficulty\tlamp\texscore\tdate\tmisscount\tnotecount\tlevel\tstyle\tstyle2\tgauge";
    private const string Row = "合成曲\tDPA\tNP\t1500\t2026/01/01 00:01:02\t-\t1000\t11\tRANDOM\tMIRROR\tEX HARD";
    public RefluxImporterTests()
    {
        Directory.CreateDirectory(directory);
        Database.Initialize();
        Sql("""
            INSERT INTO songs(tag,title,normalized_title) VALUES ('one','合成曲','合成曲');
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes,is_active)
            SELECT 'one',s.column1,d.column1,11,1000,0
            FROM (VALUES ('SP'),('DP')) s CROSS JOIN (VALUES ('B'),('N'),('H'),('A'),('L')) d;
            """);
    }
    private void Write(params string[] rows) => File.WriteAllText(Input, Header + "\n" + string.Join("\n", rows) + (rows.Length > 0 ? "\n" : ""));
    private object? Sql(string sql)
    {
        using var c = Database.OpenConnection();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public async Task ReimportAppendCopyAndDifferentSessionPreserveEveryPlay()
    {
        Write(Row, Row, Row.Replace("1500", "1400").Replace("\t-\t", "\t0\t"));
        var original = SHA256.HashData(File.ReadAllBytes(Input));
        var first = await Importer.ImportAsync(Input, id);
        Assert.Equal((3, 3, "SUCCESS"), (first.Read, first.Imported, first.Status));
        Assert.True(File.Exists(first.BackupPath));
        Assert.Equal(3, (await Importer.ImportAsync(Input, id)).Duplicates);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(Input)));
        // コピー・改名、BOM・改行の違いは同じIDで再取込できる。
        var copy = Path.Combine(directory, "copy.tsv");
        File.WriteAllText(copy, File.ReadAllText(Input).Replace("\n", "\r\n"), new UTF8Encoding(true));
        Assert.Equal(3, (await Importer.ImportAsync(copy, id)).Duplicates);
        File.AppendAllText(Input, Row + "\n");
        Assert.Equal((1, 3), ToCounts(await Importer.ImportAsync(Input, id)));
        Assert.Equal(3, (await Importer.ImportAsync(copy, Guid.NewGuid())).Imported);
        Assert.Equal(7L, Sql("SELECT COUNT(*) FROM play_history"));
        Assert.Equal(2L, Sql("SELECT COUNT(*) FROM play_history WHERE miss_count=0"));
        Assert.Equal(5L, Sql("SELECT COUNT(*) FROM play_history WHERE miss_count IS NULL"));
        Assert.Equal(7L, Sql("SELECT COUNT(*) FROM play_history WHERE clear_lamp=0 AND score>0 AND option_style_1='RANDOM' AND option_style_2='MIRROR' AND gauge_type='EX HARD'"));
        Assert.Equal("2026-01-01T00:01:02Z", Sql("SELECT played_at FROM play_history LIMIT 1"));
    }
    private static (int, int) ToCounts(RefluxImportResult result) => (result.Imported, result.Duplicates);

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("insert")]
    [InlineData("header")]
    [InlineData("time")]
    public async Task ChangesHoldEntireSessionAndDoNotReplaceBaseline(string change)
    {
        Write(Row, Row);
        await Importer.ImportAsync(Input, id);
        var options = new RefluxImportOptions();
        switch (change)
        {
            case "edit": Write(Row.Replace("1500", "1200"), Row, Row); break;
            case "delete": Write(); break;
            case "insert": Write(Row.Replace("1500", "1300"), Row, Row); break;
            case "header": File.WriteAllText(Input, File.ReadAllText(Input).Replace("style2", "unknown")); break;
            case "time": options = new(RefluxTimeMode.Local, "Tokyo Standard Time"); break;
        }
        var result = await Importer.ImportAsync(Input, id, options);
        Assert.True(result.SessionHeld);
        Assert.Equal("PARTIAL", result.Status);
        Assert.Equal(0, result.Imported);
        Assert.Equal(2L, Sql("SELECT COUNT(*) FROM play_history"));
        Assert.Equal(1L, Sql("SELECT COUNT(*) FROM unresolved_imports WHERE reason_code='SESSION_CHANGED'"));
        Write(Row, Row, Row);
        Assert.Equal((1, 2), ToCounts(await Importer.ImportAsync(Input, id)));
    }

    [Fact]
    public async Task UnresolvedCanBeRetriedButInputCorrectionsAreHeld()
    {
        Write(Row.Replace("合成曲", "別名"), Row.Replace("\tNP\t", "\tUNKNOWN\t"), "broken");
        var first = await Importer.ImportAsync(Input, id);
        Assert.Equal((1, 2, "PARTIAL"), (first.Unresolved, first.Invalid, first.Status));
        Sql("INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES ('one','別名','別名','manual')");
        var second = await Importer.ImportAsync(Input, id);
        Assert.Equal(1, second.Imported);
        Assert.Equal(1L, Sql("SELECT COUNT(*) FROM unresolved_imports WHERE status='RESOLVED'"));
        Assert.Equal(1, (await Importer.ImportAsync(Input, id)).Duplicates);
        Write(Row.Replace("合成曲", "別名"), Row, "broken");
        Assert.True((await Importer.ImportAsync(Input, id)).SessionHeld);
    }

    [Theory]
    [InlineData("title\tlamp\n")]
    [InlineData("title\tdifficulty\tlamp\texscore\tdate\npartial")]
    public async Task InvalidFilesFailWithoutPartialInsert(string text)
    {
        File.WriteAllText(Input, text);
        await Assert.ThrowsAsync<InvalidDataException>(() => Importer.ImportAsync(Input, id));
        Assert.Equal(0L, Sql("SELECT COUNT(*) FROM play_history"));
        Assert.Equal("FAILED", Sql("SELECT status FROM import_runs"));
    }

    [Fact]
    public async Task CancellationAndProgressFailureRollbackThenAllowRetry()
    {
        Write(Row, Row);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Importer.ImportAsync(Input, id,
            cancellationToken: cancel.Token, progress: new InlineProgress(_ => cancel.Cancel())));
        Assert.Equal(0L, Sql("SELECT COUNT(*) FROM play_history"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Importer.ImportAsync(Input, id,
            progress: new InlineProgress(_ => throw new InvalidOperationException("合成障害"))));
        Assert.Equal(2L, Sql("SELECT COUNT(*) FROM import_runs WHERE status='FAILED' AND records_imported=0"));
        Assert.Equal(2, (await Importer.ImportAsync(Input, id)).Imported);
    }

    [Fact]
    public async Task ConcurrentImportsAreSerializedAndBackupIsRestorable()
    {
        Write(Row, Row);
        var results = await Task.WhenAll(Importer.ImportAsync(Input, id), Importer.ImportAsync(Input, id));
        Assert.Equal(2, results.Sum(r => r.Imported));
        Assert.Equal(2, results.Sum(r => r.Duplicates));
        var first = results.Single(r => r.Imported == 2);
        var restored = Path.Combine(directory, "restored.db");
        File.Copy(first.BackupPath, restored);
        var db = new DatabaseInitializer(restored);
        db.Initialize();
        using var c = db.OpenConnection();
        using var command = c.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM play_history";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task BackupFailureAndSameInputOutputDoNotWriteLogs()
    {
        Write(Row);
        var blocked = Path.Combine(directory, "not-directory");
        File.WriteAllText(blocked, "x");
        await Assert.ThrowsAnyAsync<IOException>(() => new RefluxSessionTsvImporter(Database, blocked).ImportAsync(Input, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Importer.ImportAsync(Database.DatabasePath, id));
        Assert.Equal(0L, Sql("SELECT COUNT(*) FROM import_runs"));
    }
    private sealed class InlineProgress(Action<int> action) : IProgress<int>
    { public void Report(int value) => action(value); }

    [Fact]
    public async Task LevelNotesAndAmbiguousTitlesRemainUnresolved()
    {
        Sql("""
            INSERT INTO songs(tag,title,normalized_title) VALUES ('two','曖昧曲','曖昧曲'),('three','曖昧曲','曖昧曲');
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes) VALUES ('two','DP','A',11,1000),('three','DP','A',11,1000);
            """);
        Write(Row.Replace("合成曲", "曖昧曲"), Row.Replace("\t11\t", "\t12\t"), Row.Replace("1000", "1001"));
        var result = await Importer.ImportAsync(Input, id);
        Assert.Equal((0, 3), (result.Imported, result.Unresolved));
        Assert.Equal(3L, Sql("SELECT COUNT(*) FROM unresolved_imports WHERE raw_data IS NOT NULL"));
    }

    [Fact]
    public async Task OptionalColumnsMapToStorageWithoutPersistingGradeAsAggregate()
    {
        File.WriteAllText(Input, "title\tdifficulty\tlamp\texscore\tdate\tplaytype\tgaugepercent\tpgreat\tgreat\tgood\tbad\tpoor\tcombobreak\tfast\tslow\tassist\trange\tgrade\n" +
            "合成曲\tSPB\tPFC\t25\t2026/01/01 00:00:01\tP2\t100\t10\t5\t0\t0\t0\t0\t2\t3\tOFF\tLIFT\tAAA\n");
        var result = await Importer.ImportAsync(Input, id, new(RefluxTimeMode.Local, "Tokyo Standard Time"));
        Assert.Equal(1, result.Imported);
        Assert.Equal(1L, Sql("""
            SELECT COUNT(*) FROM play_history WHERE played_at='2025-12-31T15:00:01Z' AND clear_lamp=7
                AND play_side='P2' AND gauge_percent=100 AND pgreat=10 AND great=5 AND good=0 AND bad=0 AND poor=0
                AND combo_break=0 AND fast=2 AND slow=3 AND assist_type='OFF' AND range_type='LIFT'
                AND level_at_play IS NULL AND total_notes_at_play IS NULL AND miss_count IS NULL
                AND json_extract(raw_data,'$.row.grade')='AAA';
            """));
    }

    [Fact]
    public async Task IncompleteAppendDoesNotAcceptPrefixOrChangeExistingHistory()
    {
        Write(Row);
        await Importer.ImportAsync(Input, id);
        File.AppendAllText(Input, Row);
        await Assert.ThrowsAsync<InvalidDataException>(() => Importer.ImportAsync(Input, id));
        Assert.Equal(1L, Sql("SELECT COUNT(*) FROM play_history"));
        File.AppendAllText(Input, "\n");
        Assert.Equal((1, 1), ToCounts(await Importer.ImportAsync(Input, id)));
    }

    [Fact]
    public async Task RunningFromInterruptedProcessIsNotTreatedAsSuccess()
    {
        Write(Row);
        Sql("INSERT INTO import_runs(source_type,source_name,status) VALUES ('REFLUX_SESSION_TSV','" + id.ToString("N") + "','RUNNING')");
        Assert.Equal(1, (await Importer.ImportAsync(Input, id)).Imported);
        Assert.Equal(1L, Sql("SELECT COUNT(*) FROM import_runs WHERE status='RUNNING'"));
        Assert.Equal(1, (await Importer.ImportAsync(Input, id)).Duplicates);
    }
    public void Dispose() => Directory.Delete(directory, true);
}

using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Master;
using IIDXProgressDashboard.Matching;
using Xunit;

namespace IIDXProgressDashboard.Tests;

/// <summary>実ファイルの合成入力からDB反映・照合まで、公開APIを接続して確認する。</summary>
public sealed class Phase2IntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Phase2IntegrationTests", Guid.NewGuid().ToString("N"));
    private string Input => Path.Combine(directory, "input");
    private string Backups => Path.Combine(directory, "backups");
    private DatabaseInitializer Database => new(Path.Combine(directory, "output.db"));
    private MasterUpdateService Service => new(Database, Backups);

    public Phase2IntegrationTests()
    {
        Directory.CreateDirectory(Input);
        Database.Initialize();
        WriteFixture();
    }

    private void WriteFixture(string title = "曲名")
    {
        foreach (var (name, content) in TextageMasterParserTests.Fixture())
            File.WriteAllText(Path.Combine(Input, name), content.Replace("曲名", title), new UTF8Encoding(false));
    }

    private Task<MasterSnapshot> Acquire(CancellationToken token) =>
        new MasterDataProvider().ReadLocalAsync(Input, new(TextageEncoding.Utf8), token);

    private async Task Seed()
    {
        await Service.ApplyAsync(await Service.PrepareAsync(Acquire));
        new SongAliasRepository(Database, Backups).Register("song", "Ａlias　Name");
        // 履歴と難易度表は後続Importerの代わりに合成参照だけを準備する。
        Execute("""
            INSERT INTO play_history(chart_id,played_at,clear_lamp,score,miss_count,source_system,source_record_key,import_run_id)
            SELECT chart_id,'2026-01-01T00:00:00Z',0,100,NULL,'synthetic','1',1
            FROM charts WHERE tag='song' AND play_style='SP' AND difficulty='A';
            INSERT INTO difficulty_tables(table_id,table_code,display_name,level,play_style,gauge_type,source_name)
            VALUES (1,'test','test',11,'SP','NORMAL','test');
            INSERT INTO difficulty_ranks(table_id,rank_code,display_name,rank_kind,sort_order)
            VALUES (1,'B','B','JIRIKI',1);
            INSERT INTO difficulty_table_entries(table_id,chart_id,rank_code)
            SELECT 1,chart_id,'B' FROM charts WHERE tag='song' AND play_style='SP' AND difficulty='A';
            """);
    }

    [Fact]
    public async Task LocalInputResolvesAllChartKindsAndPreservesReferencesAfterRename()
    {
        await Seed();
        var ids = ReadTable("charts");
        var references = References();
        WriteFixture("Ｎew　Title");
        await Service.ApplyAsync(await Service.PrepareAsync(Acquire));
        var resolver = new ChartResolver(Database);
        var distinctIds = new HashSet<long>();
        foreach (var style in new[] { "SP", "DP" })
        foreach (var difficulty in new[] { "B", "N", "H", "A", "L" })
        {
            var expected = Scalar($"SELECT chart_id FROM charts WHERE tag='song' AND play_style='{style}' AND difficulty='{difficulty}';");
            var result = resolver.Resolve(new(" new title 副題 ", style + difficulty));
            Assert.True(result.IsResolved);
            Assert.Equal(expected, result.ChartId);
            Assert.Equal(result.ChartId, resolver.Resolve(new("alias name", style + difficulty)).ChartId);
            distinctIds.Add(result.ChartId!.Value);
        }
        Assert.Equal(10, distinctIds.Count);
        Assert.Equal(references, References());
        // 更新時刻以外の譜面情報と識別子を比較する。
        Assert.Equal(JsonSerializer.Serialize(ids.Select(r => r[..7])), JsonSerializer.Serialize(ReadTable("charts").Select(r => r[..7])));
        Assert.Equal("Ｎew Title 副題", Scalar("SELECT title FROM songs;"));
        Assert.Equal("NEW TITLE 副題", Scalar("SELECT normalized_title FROM songs;"));
        Assert.Null(Scalar("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("parser")]
    [InlineData("validation")]
    [InlineData("apply")]
    [InlineData("cancel")]
    public async Task FailedStageStopsPipelineAndPreservesAllExistingRows(string stage)
    {
        await Seed();
        var before = DomainState();
        WriteFixture("Changed");
        if (stage == "reader") File.WriteAllBytes(Path.Combine(Input, "datatbl.js"), [0xff]);
        if (stage == "parser") File.WriteAllText(Path.Combine(Input, "datatbl.js"), "datatbl={'song':");
        using var cancellation = new CancellationTokenSource();
        var reachedApply = false;
        var reachedResolve = false;
        var error = await Record.ExceptionAsync(async () =>
        {
            var plan = await Service.PrepareAsync(async token =>
            {
                var snapshot = await Acquire(token);
                return stage == "validation" ? snapshot with { Charts = [] } : snapshot;
            });
            reachedApply = true;
            // 同期progressで書込み開始後に失敗させ、transaction全体のrollbackを確認する。
            await Service.ApplyAsync(plan, cancellationToken: cancellation.Token,
                progress: new InlineProgress(_ =>
                {
                    if (stage == "apply") throw new InvalidOperationException("synthetic apply failure");
                    if (stage == "cancel") cancellation.Cancel();
                }));
            reachedResolve = true;
            new ChartResolver(Database).Resolve(new("Changed 副題", "SPA"));
        });
        Assert.NotNull(error);
        Assert.Equal(stage is "apply" or "cancel", reachedApply);
        Assert.False(reachedResolve);
        Assert.Equal(before, DomainState());
        Assert.Equal("FAILED", Scalar("SELECT status FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        var message = Assert.IsType<string>(Scalar("SELECT message FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        Assert.False(string.IsNullOrWhiteSpace(message));
        if (stage == "cancel") Assert.StartsWith("CANCELLED:", message);
        Assert.Equal(0L, Scalar("SELECT records_imported FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
        Assert.Null(Scalar("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData("missing", "SPA", null, "SONG_NOT_FOUND")]
    [InlineData("曲名 副題", "SPX", null, "INVALID_DIFFICULTY")]
    [InlineData("曲名 副題", "SPA", 12, "LEVEL_MISMATCH")]
    public async Task UnresolvedInputReturnsReasonWithoutChangingCommittedMaster(string title, string difficulty, int? level, string reason)
    {
        await Seed();
        var before = DomainState();
        var result = new ChartResolver(Database).Resolve(new(title, difficulty, Level: level));
        Assert.False(result.IsResolved);
        Assert.Null(result.ChartId);
        Assert.Equal(reason, Assert.Single(result.Issues).Code);
        Assert.Equal(before, DomainState());
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM unresolved_imports;"));
    }

    // 失敗ログは追加されるため、保全対象テーブルの全列・全行を比較する。
    private string DomainState() => JsonSerializer.Serialize(new[] { "songs", "charts", "play_history", "song_aliases",
        "difficulty_tables", "difficulty_ranks", "difficulty_table_entries" }.Select(ReadTable));
    private string References() => JsonSerializer.Serialize(new[] { "play_history", "song_aliases", "difficulty_table_entries" }.Select(ReadTable));
    private object[][] ReadTable(string table)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY 1;";
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
        return rows.ToArray();
    }
    private object? Scalar(string sql)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
    private void Execute(string sql)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private sealed class InlineProgress(Action<int> action) : IProgress<int>
    {
        public void Report(int value) => action(value);
    }
    public void Dispose() => Directory.Delete(directory, true);
}

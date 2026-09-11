using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Matching;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class ChartResolverTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private DatabaseInitializer Database => new(Path.Combine(directory, "output.db"));
    private ChartResolver Resolver => new(Database);

    public ChartResolverTests()
    {
        Database.Initialize();
        // 全10種の譜面と、非アクティブ・同名・任意値不明の境界を合成DBに用意する。
        Execute("""
            INSERT INTO songs(tag,title,normalized_title,is_active) VALUES
            ('one','Ａ Song','old',1),('two','Other','OTHER',0),('unknown','Unknown','UNKNOWN',1);
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes,is_active)
            SELECT 'one',s.column1,d.column1,12,1000,1
            FROM (VALUES ('SP'),('DP')) s CROSS JOIN (VALUES ('B'),('N'),('H'),('A'),('L')) d;
            INSERT INTO charts(tag,play_style,difficulty,level,total_notes,is_active) VALUES
            ('two','SP','A',11,900,0),('unknown','SP','A',NULL,NULL,1);
            INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES
            ('one','Alias','ALIAS','reflux'),('two','Alias','ALIAS','legacy');
            """);
    }

    public static IEnumerable<object[]> ChartKinds()
    {
        var names = new[] { "BEGINNER", "NORMAL", "HYPER", "ANOTHER", "LEGGENDARIA" };
        foreach (var style in new[] { "SP", "DP" })
            for (var i = 0; i < 5; i++)
                yield return new object[] { style, "BNHAL"[i].ToString(), names[i] };
    }

    [Theory]
    [MemberData(nameof(ChartKinds))]
    public void EveryChartKindResolvesToItsOwnStableId(string style, string difficulty, string longName)
    {
        var result = Resolver.Resolve(new(" a　song ", style + difficulty, Level: 12, TotalNotes: 1000));
        Assert.True(result.IsResolved);
        Assert.Empty(result.Issues);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(style, candidate.PlayStyle);
        Assert.Equal(difficulty, candidate.Difficulty);
        Assert.Equal(candidate.ChartId, result.ChartId);
        Assert.Equal(result.ChartId, Resolver.Resolve(new("A Song", difficulty, style)).ChartId);
        Assert.Equal(result.ChartId, Resolver.Resolve(new(null, longName, style, Tag: "one")).ChartId);
    }

    [Theory]
    [InlineData("SB")]
    [InlineData("SBo")]
    [InlineData("SPX")]
    [InlineData("spa")]
    [InlineData(" SPA ")]
    [InlineData("A")]
    [InlineData(null)]
    public void InvalidDifficultyNeverFallsBack(string? difficulty) =>
        AssertFailure(new("A Song", difficulty), "INVALID_DIFFICULTY");

    [Fact]
    public void ConflictingStyleAndInvalidOptionalInputReturnSpecificReasons()
    {
        AssertFailure(new("A Song", "SPA", "DP"), "INVALID_DIFFICULTY");
        AssertFailure(new("A Song", "A", "XX"), "INVALID_DIFFICULTY");
        AssertFailure(new("A Song", "SPA", Level: 0), "INVALID_LEVEL");
        AssertFailure(new("A Song", "SPA", Level: 13), "INVALID_LEVEL");
        AssertFailure(new("A Song", "SPA", TotalNotes: -1), "INVALID_NOTES");
        AssertFailure(new("A Song", "SPA", SourceName: " manual "), "INVALID_SOURCE");
        AssertFailure(new("A Song", "SPA", Tag: ""), "INVALID_TAG");
        AssertFailure(new(null, "SPA"), "INVALID_TITLE");
        AssertFailure(new("　", "SPA", Tag: "one"), "INVALID_TITLE");
        AssertFailure(new("\ud800", "SPA"), "INVALID_TITLE");
    }

    [Fact]
    public void MissingSongChartAndTagHaveDifferentReasons()
    {
        AssertFailure(new("A-Song", "SPA"), "SONG_NOT_FOUND");
        AssertFailure(new("Other", "SPH"), "CHART_NOT_FOUND");
        AssertFailure(new("A Song", "SPA", Tag: "missing"), "TAG_NOT_FOUND");
        AssertFailure(new("A Song", "SPA", Tag: "two"), "TAG_TITLE_MISMATCH");
        AssertFailure(new("missing", "SPA", Tag: "one"), "TAG_TITLE_MISMATCH");
    }

    [Fact]
    public void AmbiguityIsNotFilteredByLevelNotesChartExistenceOrActiveState()
    {
        Execute("UPDATE songs SET title='a song' WHERE tag='two';");
        var ambiguous = AssertFailure(new("Ａ Song", "SPA", Level: 12, TotalNotes: 1000), "AMBIGUOUS_SONG");
        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.Equal(2, ambiguous.TitleEvidence.Count);
        AssertFailure(new("A Song", "SPH"), "AMBIGUOUS_SONG");
        // 明示tagは同名候補の一つなら利用できるが、任意値矛盾を無視しない。
        Assert.True(Resolver.Resolve(new("A Song", "SPA", Tag: "one")).IsResolved);
        AssertFailure(new("A Song", "SPA", Tag: "two", Level: 12), "LEVEL_MISMATCH");
    }

    [Fact]
    public void AliasSourceAndManualCollisionsUseSharedTitlePolicy()
    {
        Assert.True(Resolver.Resolve(new("Alias", "SPA", SourceName: "reflux")).IsResolved);
        AssertFailure(new("Alias", "SPA"), "AMBIGUOUS_SONG");
        AssertFailure(new("Alias", "SPA", SourceName: "unknown"), "SONG_NOT_FOUND");
        Execute("INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES ('two','Alias','ALIAS','manual');");
        AssertFailure(new("Alias", "SPA", SourceName: "reflux"), "AMBIGUOUS_SONG");
        Execute("INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES ('two','A Song','A SONG','manual');");
        AssertFailure(new("A Song", "SPA"), "AMBIGUOUS_SONG");
    }

    [Fact]
    public void MismatchesKeepBothReasonsAndNeverSubstituteAnotherChart()
    {
        var request = new ChartResolutionRequest("A Song", "SPA", Level: 11, TotalNotes: 900);
        var result = Resolver.Resolve(request);
        Assert.False(result.IsResolved);
        Assert.Null(result.ChartId);
        Assert.Same(request, result.Request);
        Assert.Equal(new[] { "LEVEL_MISMATCH", "NOTES_MISMATCH" }, result.Issues.Select(i => i.Code));
        Assert.All(result.Issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Detail)));
        Assert.Equal("one", Assert.Single(result.Candidates).Tag);
        AssertFailure(new("A Song", "SPA", TotalNotes: 0), "NOTES_MISMATCH");
    }

    [Fact]
    public void UnknownOptionalValuesAndInactiveChartsAreValidForHistory()
    {
        Assert.True(Resolver.Resolve(new("A Song", "SPA")).IsResolved);
        Assert.True(Resolver.Resolve(new("Unknown", "SPA", Level: 12, TotalNotes: 1000)).IsResolved);
        var inactive = Resolver.Resolve(new("Other", "SPA"));
        Assert.True(inactive.IsResolved);
        Assert.False(Assert.Single(inactive.Candidates).SongIsActive);
        Assert.False(Assert.Single(inactive.Candidates).ChartIsActive);
        Execute("UPDATE charts SET total_notes=0 WHERE tag='unknown';");
        Assert.True(Resolver.Resolve(new("Unknown", "SPA", TotalNotes: 0)).IsResolved);
        AssertFailure(new("Unknown", "SPA", TotalNotes: 1), "NOTES_MISMATCH");
    }

    [Fact]
    public void ResolutionHasNoPersistentSideEffectsAndSqlInputIsLiteral()
    {
        var before = File.ReadAllBytes(Database.DatabasePath);
        Resolver.Resolve(new("A Song", "SPA"));
        AssertFailure(new("'; DROP TABLE songs; --", "SPA", Tag: "'", SourceName: "source'"), "TAG_NOT_FOUND");
        Assert.Equal(before, File.ReadAllBytes(Database.DatabasePath));
    }

    [Fact]
    public void MissingEmptyAndUnknownDatabasesAreNotInitializedOrReportedAsUnresolved()
    {
        // 運用エラーは入力行の未解決と区別し、原本をそのまま保つ。
        var path = Path.Combine(directory, "not-initialized.db");
        var resolver = new ChartResolver(new DatabaseInitializer(path));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => resolver.Resolve(new("A Song", "SPA")));
        Assert.False(File.Exists(path));
        File.WriteAllBytes(path, []);
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(new("A Song", "SPA")));
        Assert.Empty(File.ReadAllBytes(path));
        using (var connection = new DatabaseInitializer(path).OpenConnection())
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "CREATE TABLE legacy(value TEXT);";
            query.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(new("A Song", "SPA")));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private ChartResolution AssertFailure(ChartResolutionRequest request, string code)
    {
        var result = Resolver.Resolve(request);
        Assert.False(result.IsResolved);
        Assert.Null(result.ChartId);
        Assert.Equal(code, Assert.Single(result.Issues).Code);
        return result;
    }

    private void Execute(string sql)
    {
        using var connection = Database.OpenConnection();
        using var query = connection.CreateCommand();
        query.CommandText = sql;
        query.ExecuteNonQuery();
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

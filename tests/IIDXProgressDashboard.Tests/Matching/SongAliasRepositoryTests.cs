using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class SongAliasRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private DatabaseInitializer Database => new(Path.Combine(directory, "output.db"));
    private SongAliasRepository Repository => new(Database, Path.Combine(directory, "backups"));

    public SongAliasRepositoryTests()
    {
        Database.Initialize();
        // 古い保存キーも再正規化して照合する。A/Bは意図的に正規化後の同名曲。
        Execute("""
            INSERT INTO songs(tag,title,normalized_title,is_active) VALUES
            ('A','Ａ Song','old-a',1),('B','a song','old-b',0),('C','Other','old-c',1);
            """);
    }

    [Fact]
    public void FormalTitleCollisionRetainsEveryTagIncludingInactiveSongs()
    {
        var result = Repository.FindCandidates(" a　SONG ");
        Assert.Equal(SongTitleMatchStatus.Ambiguous, result.Status);
        Assert.Equal(new[] { "A", "B" }, result.Tags);
        Assert.Equal("Ａ Song", result.Evidence[0].OriginalTitle);
        Assert.Equal("old-a", Scalar("SELECT normalized_title FROM songs WHERE tag='A';"));
        Assert.Equal(SongTitleMatchStatus.NotFound, Repository.FindCandidates("missing").Status);
        Assert.Equal(SongTitleMatchStatus.NotFound, Repository.FindCandidates(" ").Status);
    }

    [Fact]
    public void RegistrationIsIdempotentAndKeepsOriginalMetadataWithBackup()
    {
        var first = Repository.Register("A", " Ａlias ", note: "original");
        var repeat = Repository.Register("A", "alias", note: "replacement");
        Assert.True(first.Added);
        Assert.False(repeat.Added);
        Assert.Equal(first.AliasId, repeat.AliasId);
        Assert.Null(repeat.BackupPath);
        Assert.Equal(" Ａlias ", Scalar("SELECT alias_title FROM song_aliases;"));
        Assert.Equal("ALIAS", Scalar("SELECT normalized_alias FROM song_aliases;"));
        Assert.Equal("original", Scalar("SELECT note FROM song_aliases;"));
        Assert.Equal(SongTitleMatchStatus.Unique, Repository.FindCandidates("alias").Status);
        using var backup = new DatabaseInitializer(first.BackupPath!).OpenConnection();
        using var query = backup.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM song_aliases;";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    [Fact]
    public void SameSourceConflictDoesNotReplaceExistingTagEvenForOldKeys()
    {
        Execute("INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name) VALUES ('A','Ａlias','old','reflux');");
        Assert.Throws<InvalidOperationException>(() => Repository.Register("B", "alias", "reflux"));
        Assert.Equal("A", Scalar("SELECT tag FROM song_aliases;"));
        Assert.Equal("old", Scalar("SELECT normalized_alias FROM song_aliases;"));
        Assert.Equal("A", Assert.Single(Repository.FindCandidates("alias", "reflux").Tags));
    }

    [Fact]
    public void SourceAndManualCandidatesHaveNoWinningPriority()
    {
        Repository.Register("A", "Alias", "reflux");
        Repository.Register("B", "Alias", "legacy");
        Assert.Equal("A", Assert.Single(Repository.FindCandidates("alias", "reflux").Tags));
        Assert.Equal("B", Assert.Single(Repository.FindCandidates("alias", "legacy").Tags));
        Assert.Equal(SongTitleMatchStatus.Ambiguous, Repository.FindCandidates("alias").Status);
        Repository.Register("C", "Alias");
        Assert.Equal(new[] { "A", "C" }, Repository.FindCandidates("alias", "reflux").Tags);
        Assert.Equal("C", Assert.Single(Repository.FindCandidates("alias", "unknown").Tags));
    }

    [Fact]
    public void AliasCannotOverrideFormalTitleAndRepeatedEvidenceIsNotAmbiguous()
    {
        Repository.Register("C", "Other", "reflux");
        Repository.Register("C", "Other");
        var same = Repository.FindCandidates("other", "reflux");
        Assert.Equal(SongTitleMatchStatus.Unique, same.Status);
        Assert.Equal(3, same.Evidence.Count);
        Repository.Register("A", "Other", "legacy");
        Assert.Equal(SongTitleMatchStatus.Ambiguous, Repository.FindCandidates("other", "legacy").Status);
    }

    [Fact]
    public void InvalidRegistrationAndBackupFailureDoNotWrite()
    {
        Assert.Throws<ArgumentException>(() => Repository.Register("missing", "alias"));
        Assert.Throws<ArgumentException>(() => Repository.Register("A", "　"));
        Assert.Throws<ArgumentException>(() => Repository.Register("A", "alias", " manual "));
        var blocked = Path.Combine(directory, "blocked");
        File.WriteAllText(blocked, "synthetic file");
        Assert.ThrowsAny<IOException>(() => new SongAliasRepository(Database, blocked).Register("A", "alias"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM song_aliases;"));
    }

    [Fact]
    public void SqlMetacharactersRemainLiteralData()
    {
        Repository.Register("A", "'; DROP TABLE songs; --", "source'", "note'");
        Assert.Equal("A", Assert.Single(Repository.FindCandidates("'; DROP TABLE songs; --", "source'").Tags));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM songs;"));
    }

    private object? Scalar(string sql)
    {
        using var connection = Database.OpenConnection();
        using var query = connection.CreateCommand();
        query.CommandText = sql;
        return query.ExecuteScalar();
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

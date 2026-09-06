using IIDXProgressDashboard.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private DatabaseInitializer Database => new(Path.Combine(directory, "iidx-progress.db"));

    [Fact]
    public void InitializationIsRepeatableAndPreservesHistory()
    {
        Database.Initialize();
        using (var connection = Database.OpenConnection()) Seed(connection);
        Database.Initialize();
        using var reopened = Database.OpenConnection();
        Assert.Equal(1L, Scalar(reopened, "PRAGMA foreign_keys;"));
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM schema_migrations WHERE version=1;"));
        Assert.Equal(10L, Scalar(reopened, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table';"));
        Assert.Equal(2L, Scalar(reopened, "SELECT COUNT(*) FROM play_history;"));
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM play_history WHERE miss_count IS NULL;"));
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM play_history WHERE miss_count=0;"));
        Assert.Equal("ok", Scalar(reopened, "PRAGMA integrity_check;"));
        Assert.Null(Scalar(reopened, "PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData("INSERT INTO charts(tag,play_style,difficulty) VALUES ('missing','SP','A');")]
    [InlineData("INSERT INTO charts(tag,play_style,difficulty) VALUES ('test','SP','A');")]
    [InlineData("INSERT INTO charts(tag,play_style,difficulty) VALUES ('test','SP','ANOTHER');")]
    [InlineData("UPDATE play_history SET clear_lamp=8;")]
    [InlineData("UPDATE play_history SET miss_count=-1;")]
    [InlineData("UPDATE play_history SET score=-1;")]
    [InlineData("UPDATE play_history SET source_record_key='first';")]
    [InlineData("DELETE FROM charts WHERE chart_id=1;")]
    [InlineData("DELETE FROM import_runs WHERE import_run_id=1;")]
    public void InvalidWritesAreRejected(string sql)
    {
        Database.Initialize();
        using var connection = Database.OpenConnection();
        Seed(connection);
        Assert.Equal(19, Assert.Throws<SqliteException>(() => Execute(connection, sql)).SqliteErrorCode);
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM play_history;"));
    }

    [Fact]
    public void ChartIdentityAndGaugeTablesRemainIndependent()
    {
        Database.Initialize();
        using var connection = Database.OpenConnection();
        Seed(connection);
        Execute(connection, """
            INSERT INTO charts(tag,play_style,difficulty) VALUES ('test','SP','H'),('test','SP','L'),('test','DP','A');
            INSERT INTO difficulty_tables(table_id,table_code,display_name,level,play_style,gauge_type,source_name)
            VALUES (1,'sp11n','Normal',11,'SP','NORMAL','test'),(2,'sp11h','Hard',11,'SP','HARD','test');
            INSERT INTO difficulty_ranks(table_id,rank_code,display_name,rank_kind,sort_order)
            VALUES (1,'B','B','JIRIKI',2),(2,'C','C','JIRIKI',1);
            INSERT INTO difficulty_table_entries(table_id,chart_id,rank_code) VALUES (1,1,'B'),(2,1,'C');
            """);
        Assert.Equal(4L, Scalar(connection, "SELECT COUNT(*) FROM charts;"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM difficulty_table_entries WHERE chart_id=1;"));
        Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE difficulty_table_entries SET rank_code='B' WHERE table_id=2;"));
    }

    [Theory]
    [InlineData("CREATE TABLE play_history(id INTEGER PRIMARY KEY, song_name TEXT); INSERT INTO play_history VALUES(1,'original');")]
    [InlineData("CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY); INSERT INTO schema_migrations VALUES(99);")]
    public void UnknownDatabaseIsNotModified(string sql)
    {
        Directory.CreateDirectory(directory);
        using (var connection = new SqliteConnection($"Data Source={Database.DatabasePath};Pooling=False"))
        {
            connection.Open();
            Execute(connection, sql);
        }
        var before = File.ReadAllBytes(Database.DatabasePath);
        Assert.Throws<InvalidOperationException>(() => Database.Initialize());
        Assert.Equal(before, File.ReadAllBytes(Database.DatabasePath));
    }

    [Theory]
    [InlineData("UPDATE schema_migrations SET version=99;")]
    [InlineData("DELETE FROM schema_migrations;")]
    [InlineData("DROP INDEX idx_play_history_bp;")]
    public void ModifiedSchemaOrMigrationHistoryIsRejected(string sql)
    {
        Database.Initialize();
        using (var connection = Database.OpenConnection()) Execute(connection, sql);
        var before = File.ReadAllBytes(Database.DatabasePath);
        Assert.Throws<InvalidOperationException>(() => Database.Initialize());
        Assert.Equal(before, File.ReadAllBytes(Database.DatabasePath));
    }

    [Fact]
    public void FailedInitialMigrationRollsBackDdlAndVersionRecord()
    {
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection($"Data Source={Database.DatabasePath};Pooling=False");
        connection.Open();
        // A TEMP view is not part of main.sqlite_schema but conflicts with CREATE TABLE.
        // Failure occurs after several earlier tables have already been created.
        Execute(connection, "CREATE TEMP VIEW songs AS SELECT 'conflict' AS tag;");
        Assert.Throws<SqliteException>(() => new MigrationRunner().Run(connection));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM main.sqlite_schema;"));
        Execute(connection, "DROP VIEW temp.songs;");
        new MigrationRunner().Run(connection);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    public void OpenConnectionDoesNotCreateMissingDatabase()
    {
        Directory.CreateDirectory(directory);
        Assert.Throws<SqliteException>(() => Database.OpenConnection());
        Assert.False(File.Exists(Database.DatabasePath));
    }

    private static void Seed(SqliteConnection connection) => Execute(connection, """
        INSERT INTO songs(tag,title,normalized_title) VALUES ('test','Test','test');
        INSERT INTO charts(chart_id,tag,play_style,difficulty,level,total_notes) VALUES(1,'test','SP','A',11,1000);
        INSERT INTO import_runs(import_run_id,source_type,source_name,status) VALUES(1,'TEST','synthetic','SUCCESS');
        INSERT INTO play_history(chart_id,played_at,clear_lamp,score,miss_count,source_system,source_record_key,import_run_id)
        VALUES(1,'2026-01-01T00:00:00Z',0,1600,NULL,'TEST','first',1),
              (1,'2026-01-01T00:01:00Z',5,1500,0,'TEST','second',1);
        """);

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

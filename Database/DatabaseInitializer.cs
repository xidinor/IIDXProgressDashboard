using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Database;

/// <summary>Explicit output path: callers must keep migration inputs separate.</summary>
public sealed class DatabaseInitializer
{
    public string DatabasePath { get; }

    public DatabaseInitializer(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open(SqliteOpenMode.ReadWriteCreate);
        new MigrationRunner().Run(connection);
    }

    /// <summary>Opens an existing database; never silently creates a missing file.</summary>
    public SqliteConnection OpenConnection() => Open(SqliteOpenMode.ReadWrite);

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

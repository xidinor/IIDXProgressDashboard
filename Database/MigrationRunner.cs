using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Database;

public sealed class MigrationRunner
{
    public const int CurrentVersion = 1;

    public void Run(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Execute(connection, null, "PRAGMA foreign_keys = ON;");
        if (Convert.ToInt32(Scalar(connection, null, "PRAGMA foreign_keys;")) != 1)
            throw new InvalidOperationException("外部キー制約を有効化できません。既存トランザクションの外で実行してください。");

        // Immediate transaction serializes schema inspection and initialization.
        using var transaction = connection.BeginTransaction(deferred: false);
        var objects = ReadSchema(connection, transaction);
        if (objects.Count == 0)
        {
            Execute(connection, transaction, ReadInitialSql());
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations(version, description) VALUES ($version, $description);";
            record.Parameters.AddWithValue("$version", CurrentVersion);
            record.Parameters.AddWithValue("$description", "Initial unified database schema");
            record.ExecuteNonQuery();
        }
        else
        {
            // Phase 1 never upgrades nonempty databases. Future upgrades need a
            // backup/recovery procedure before adding migration steps here.
            ValidateExisting(connection, transaction, objects);
        }
        transaction.Commit();
    }

    private static void ValidateExisting(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<string, string> objects)
    {
        using var expected = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        expected.Open();
        Execute(expected, null, ReadInitialSql());
        var expectedSchema = ReadSchema(expected, null);
        if (objects.Count != expectedSchema.Count || expectedSchema.Any(pair =>
                !objects.TryGetValue(pair.Key, out var sql) || sql != pair.Value))
            throw new InvalidOperationException("旧形式または未知のDBスキーマです。初期化せず、新しい出力先を指定してください。");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != CurrentVersion || reader.Read())
            throw new InvalidOperationException("対応していないMigration履歴です。DBは変更されていません。");
    }

    private static Dictionary<string, string> ReadSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type || ':' || name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    private static string ReadInitialSql()
    {
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(
            "IIDXProgressDashboard.Database.Migrations.001_initial.sql")
            ?? throw new InvalidOperationException("初期DDLリソースが見つかりません。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

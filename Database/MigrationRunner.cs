using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Database;

/// <summary>空のDBにv1を適用し、既存DBでは構造と適用履歴を検証する。</summary>
public sealed class MigrationRunner
{
    public const int CurrentVersion = 1;

    public void Run(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // Runnerが直接呼ばれた場合も外部キーを有効にする。トランザクション開始前に確認する。
        Execute(connection, null, "PRAGMA foreign_keys = ON;");
        if (Convert.ToInt32(Scalar(connection, null, "PRAGMA foreign_keys;")) != 1)
            throw new InvalidOperationException("外部キー制約を有効化できません。既存トランザクションの外で実行してください。");

        // 最初に書き込み用トランザクションを開始し、検査と初期化の間の別書き込みを防ぐ。
        // Commitまでに例外が起きれば、usingによる破棄時に変更をロールバックする。
        using var transaction = connection.BeginTransaction(deferred: false);
        var objects = ReadSchema(connection, transaction);
        if (objects.Count == 0)
        {
            // 空のDBだけに初期DDLを適用し、同じトランザクションでバージョンを記録する。
            // テーブル作成だけ成功して「適用済み」の記録が欠ける状態を残さない。
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
            // 既存DBは検証のみ。Phase 1では旧形式や将来バージョンの変換を行わない。
            // 更新処理を追加するときは、先にバックアップと復旧手順を用意する。
            ValidateExisting(connection, transaction, objects);
        }
        // 作成・記録または既存DBの検証がすべて成功した場合だけ確定する。
        transaction.Commit();
    }

    private static void ValidateExisting(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<string, string> objects)
    {
        // 埋め込みDDLからメモリー上に見本を作り、ファイル名ではなく実際の構造で判定する。
        using var expected = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        expected.Open();
        Execute(expected, null, ReadInitialSql());
        var expectedSchema = ReadSchema(expected, null);
        // オブジェクトの増減とSQL定義を厳密に比較する。同じ意味の別表記も不一致となり得る。
        if (objects.Count != expectedSchema.Count || expectedSchema.Any(pair =>
                !objects.TryGetValue(pair.Key, out var sql) || sql != pair.Value))
            throw new InvalidOperationException("旧形式または未知のDBスキーマです。初期化せず、新しい出力先を指定してください。");

        // 構造が一致しても、適用履歴が「version 1の1行だけ」でなければ受け付けない。
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != CurrentVersion || reader.Read())
            throw new InvalidOperationException("対応していないMigration履歴です。DBは変更されていません。");
    }

    private static Dictionary<string, string> ReadSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        // SQLite内部用の名前を除外し、「種別:名前 → 作成SQL」の辞書で構造を取り出す。
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
        // ビルド時に埋め込んだSQLを読み、実行場所や外部SQLファイルの配置に依存させない。
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(
            "IIDXProgressDashboard.Database.Migrations.001_initial.sql")
            ?? throw new InvalidOperationException("初期DDLリソースが見つかりません。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        // PRAGMAなど、先頭行の先頭列だけが必要な問い合わせに使う。
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        // DDLや設定など、結果行を読み取らないSQLを指定されたトランザクション内で実行する。
        // transactionがnullの場合は、明示的なトランザクションを割り当てない。
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

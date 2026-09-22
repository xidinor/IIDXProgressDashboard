using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Database;

/// <summary>既知の構造・履歴を検証し、バックアップ後に未適用Migrationを順番に適用する。</summary>
public sealed class MigrationRunner
{
    public const int CurrentVersion = 2;

    private static readonly string[] SqlFiles = ["001_initial.sql", "002_external_song_ids.sql"];
    private static readonly string[] Descriptions = ["Initial unified database schema", "Add external song identifiers"];

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
        var version = objects.Count == 0 ? 0 : ValidateExisting(connection, transaction, objects);
        if (version > 0 && version < CurrentVersion)
            BackupBeforeMigration(connection);
        for (var next = version + 1; next <= CurrentVersion; next++)
        {
            // DDLと版の記録は一括確定する。途中失敗なら、元の版まで全体を戻す。
            // テーブル作成だけ成功して「適用済み」の記録が欠ける状態を残さない。
            Execute(connection, transaction, ReadSql(next));
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations(version, description) VALUES ($version, $description);";
            record.Parameters.AddWithValue("$version", next);
            record.Parameters.AddWithValue("$description", Descriptions[next - 1]);
            record.ExecuteNonQuery();
        }
        // 作成・記録または既存DBの検証がすべて成功した場合だけ確定する。
        transaction.Commit();
    }

    private static int ValidateExisting(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<string, string> objects)
    {
        if (!objects.ContainsKey("table:schema_migrations"))
            throw new InvalidOperationException("旧形式または未知のDBスキーマです。DBは変更されていません。");
        // 履歴テーブル自体の定義を確認してから問い合わせる。
        // 埋め込みDDLからメモリー上に見本を作り、ファイル名ではなく実際の構造で判定する。
        using var expected = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        expected.Open();
        Execute(expected, null, ReadSql(1));
        if (objects["table:schema_migrations"] != ReadSchema(expected, null)["table:schema_migrations"])
            throw new InvalidOperationException("未知のMigration履歴テーブルです。DBは変更されていません。");
        var version = 0;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt64(0) != version + 1 || version == CurrentVersion)
                    throw new InvalidOperationException("対応していないMigration履歴です。DBは変更されていません。");
                version++;
            }
        }
        if (version == 0)
            throw new InvalidOperationException("Migration履歴が空です。DBは変更されていません。");
        for (var next = 2; next <= version; next++) Execute(expected, null, ReadSql(next));
        var expectedSchema = ReadSchema(expected, null);
        // オブジェクトの増減とSQL定義を厳密に比較する。同じ意味の別表記も不一致となり得る。
        if (objects.Count != expectedSchema.Count || expectedSchema.Any(pair =>
                !objects.TryGetValue(pair.Key, out var sql) || sql != pair.Value))
            throw new InvalidOperationException("旧形式または未知のDBスキーマです。初期化せず、新しい出力先を指定してください。");

        return version;
    }

    private static void BackupBeforeMigration(SqliteConnection connection)
    {
        // ファイルDBはSQLiteのバックアップAPIでWAL内の確定済みデータも保存する。
        // 呼出し元の書込予約ロックを保持し、検証・バックアップ・更新間の別書込みを防ぐ。
        // 同じ接続でBackupDatabaseを呼ぶと書込トランザクションと競合するため読取専用接続を使う。
        var path = connection.DataSource;
        if (string.IsNullOrEmpty(path) || path == ":memory:" ||
            new SqliteConnectionStringBuilder(connection.ConnectionString).Mode == SqliteOpenMode.Memory) return;
        var backupPath = path + $".pre-v{CurrentVersion}-{Guid.NewGuid():N}.bak";
        // 失敗途中のファイルを、復旧可能なバックアップと誤認しない名前で残す。
        var pendingPath = backupPath + ".incomplete";
        using (new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = pendingPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        source.Open();
        backup.Open();
        source.BackupDatabase(backup);
        if (!Equals(Scalar(backup, null, "PRAGMA quick_check;"), "ok"))
            throw new InvalidOperationException("バックアップの検証に失敗したためMigrationを中止しました。");
        backup.Close();
        File.Move(pendingPath, backupPath);
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

    private static string ReadSql(int version)
    {
        // ビルド時に埋め込んだSQLを読み、実行場所や外部SQLファイルの配置に依存させない。
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(
            "IIDXProgressDashboard.Database.Migrations." + SqlFiles[version - 1])
            ?? throw new InvalidOperationException("MigrationのDDLリソースが見つかりません。");
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

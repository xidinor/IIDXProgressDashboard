using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Database;

/// <summary>指定された出力先のDBを初期化し、外部キー制約が有効な接続を提供する。</summary>
/// <remarks>入力原本とは別の出力先を呼び出し側で指定する。</remarks>
public sealed class DatabaseInitializer
{
    public string DatabasePath { get; }

    public DatabaseInitializer(string databasePath)
    {
        // 作成時に絶対パスへ固定し、その後の作業ディレクトリ変更の影響を受けないようにする。
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public void Initialize()
    {
        // 初期化時だけファイルの新規作成を許可し、スキーマの判断・作成はRunnerへ委譲する。
        // このメソッドで使う接続は、成功・失敗にかかわらずusingで解放する。
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open(SqliteOpenMode.ReadWriteCreate);
        new MigrationRunner().Run(connection);
    }

    /// <summary>既存DBを開く。存在しなければエラーとし、空のDBを勝手に作らない。</summary>
    /// <remarks>返された接続は呼び出し側がDisposeする。スキーマ検証はInitializeで行う。</remarks>
    public SqliteConnection OpenConnection() => Open(SqliteOpenMode.ReadWrite);

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        // 外部キーは接続ごとの設定。プールを使わず、Dispose時に接続を解放する。
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
            // Openに失敗した場合は呼び出し側へ接続を返せないため、ここで解放して例外を伝える。
            connection.Dispose();
            throw;
        }
    }
}

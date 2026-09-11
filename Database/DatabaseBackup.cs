using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Database;

/// <summary>WAL内の確定済み変更も含め、SQLiteのバックアップAPIで独立した復旧用DBを作る。</summary>
public static class DatabaseBackup
{
    public static string Create(DatabaseInitializer database, string directory)
    {
        var destination = Path.Combine(Path.GetFullPath(directory), $"master-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.db");
        if (string.Equals(database.DatabasePath, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("バックアップ先と更新先が同じです。");
        Directory.CreateDirectory(directory);
        // CreateNewで既存ファイルを上書きしない。失敗時のファイルも自動削除せず調査用に残す。
        using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) { }
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        source.Open();
        target.Open();
        source.BackupDatabase(target);
        using var check = target.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("バックアップの整合性検査に失敗しました。");
        return destination;
    }
}

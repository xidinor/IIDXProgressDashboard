using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Import;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Dashboard;

/// <summary>画面起動とは分離した明示的な事前準備。入力はSQLite読取専用接続。</summary>
public static class BetaPreparation
{
    public static async Task<LegacyImportResult> PrepareAsync(string directory, string seed, string legacy)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        using var gate = new FileStream(Path.Combine(directory, "beta-preparation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var settings = new BetaSettings(directory, "iidx-progress.db", "legacy-history.db", Guid.NewGuid(), false);
        if (File.Exists(Path.Combine(directory, BetaSettings.FileName)) || File.Exists(settings.DatabasePath) || File.Exists(settings.LegacyPath))
            throw new IOException("既存のベータ設定またはDBがあります。新しい配置先で準備してください。既存データは上書きしません。");
        // seedはPhase 1～5で準備した新形式マスター・難易度表DB。個人履歴入りseedを混ぜない。
        using (var source = OpenReadOnly(seed))
        using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM play_history;";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidDataException("seedには履歴なしの新形式DBを指定してください。");
            command.CommandText = "SELECT count(*) FROM charts;";
            if (Convert.ToInt64(command.ExecuteScalar()) == 0) throw new InvalidDataException("seedのマスターが未準備です。");
            command.CommandText = "SELECT count(*) FROM schema_migrations;"; command.ExecuteScalar();
        }
        settings.Create();
        CopyDatabase(seed, settings.DatabasePath);
        CopyDatabase(legacy, settings.LegacyPath);
        var database = new DatabaseInitializer(settings.DatabasePath);
        database.Initialize();
        return await new LegacyInfinitasLogImporter(database, directory).ImportAsync(settings.LegacyPath, settings.SourceId);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open(); return connection;
    }

    private static void CopyDatabase(string input, string output)
    {
        // CreateNewで上書きを拒否し、WALを含む確定snapshotをコピーする。
        using (new FileStream(output, FileMode.CreateNew, FileAccess.Write)) { }
        using var source = OpenReadOnly(input);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = output, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true }.ToString());
        target.Open(); source.BackupDatabase(target);
    }
}

using IIDXProgressDashboard.Matching;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Database;

public sealed record SongAliasRegistration(long AliasId, bool Added, string? BackupPath);

/// <summary>aliasの追加と曲名候補の参照。衝突時に既存の対応を上書きしない。</summary>
public sealed class SongAliasRepository
{
    public const string ManualSource = "manual";
    private readonly DatabaseInitializer database;
    private readonly string backupDirectory;

    public SongAliasRepository(DatabaseInitializer database, string backupDirectory)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        this.backupDirectory = Path.GetFullPath(backupDirectory);
    }

    public SongAliasRegistration Register(string tag, string aliasTitle, string sourceName = ManualSource, string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ValidateSource(sourceName);
        var key = TitleNormalizer.Normalize(aliasTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var connection = OpenValidated();
        // 衝突確認から追加まで書込ロックを保持し、同時登録による先勝ちを防ぐ。
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var query = Command(connection, transaction,
            "SELECT alias_id,tag,alias_title,normalized_alias FROM song_aliases WHERE source_name=$source;", ("$source", sourceName)))
        using (var reader = query.ExecuteReader())
        {
            long? existingId = null;
            while (reader.Read())
            {
                if (TitleNormalizer.Normalize(reader.GetString(2)) != key && reader.GetString(3) != key) continue;
                if (reader.GetString(1) != tag)
                    throw new InvalidOperationException($"出典 {sourceName} のaliasは既に別tag {reader.GetString(1)} と衝突しています。");
                existingId ??= reader.GetInt64(0);
            }
            // 同じ対応の再登録では、原表記・注記・作成日時も維持する。
            if (existingId.HasValue) return new(existingId.Value, false, null);
        }
        using (var exists = Command(connection, transaction, "SELECT 1 FROM songs WHERE tag=$tag;", ("$tag", tag)))
            if (exists.ExecuteScalar() is null) throw new ArgumentException("登録先のtagが存在しません。", nameof(tag));
        var backup = DatabaseBackup.Create(database, backupDirectory);
        using var insert = Command(connection, transaction, """
            INSERT INTO song_aliases(tag,alias_title,normalized_alias,source_name,note)
            VALUES ($tag,$title,$key,$source,$note) RETURNING alias_id;
            """, ("$tag", tag), ("$title", aliasTitle), ("$key", key), ("$source", sourceName), ("$note", note));
        var id = (long)insert.ExecuteScalar()!;
        transaction.Commit();
        return new(id, true, backup);
    }

    /// <summary>出典指定時はその出典とmanual、未指定時は全出典を正式タイトル候補と合併する。</summary>
    public SongTitleMatch FindCandidates(string externalTitle, string? sourceName = null)
    {
        if (sourceName is not null) ValidateSource(sourceName);
        var key = TitleNormalizer.Normalize(externalTitle);
        if (key.Length == 0) return new([]);
        using var connection = OpenValidated();
        using var transaction = connection.BeginTransaction(deferred: true);
        return FindCandidates(connection, transaction, externalTitle, sourceName);
    }

    // Resolverも同じ読取snapshot内でタイトル候補と譜面を検証する。
    internal static SongTitleMatch FindCandidates(SqliteConnection connection, SqliteTransaction transaction,
        string externalTitle, string? sourceName)
    {
        if (sourceName is not null) ValidateSource(sourceName);
        var key = TitleNormalizer.Normalize(externalTitle);
        var evidence = new List<SongTitleEvidence>();
        // Phase 2-3の任意規則で保存されたキーもあるため、原表記から共通規則で読む。
        // DBを暗黙に再索引化せず、非アクティブ曲も過去履歴の候補として保持する。
        using (var query = Command(connection, transaction, "SELECT tag,title FROM songs;"))
        using (var reader = query.ExecuteReader())
            while (reader.Read())
                if (TitleNormalizer.Normalize(reader.GetString(1)) == key)
                    evidence.Add(new(reader.GetString(0), reader.GetString(1), null));
        using (var query = Command(connection, transaction, """
            SELECT tag,alias_title,source_name FROM song_aliases
            WHERE $source IS NULL OR source_name=$source OR source_name=$manual;
            """, ("$source", sourceName), ("$manual", ManualSource)))
        using (var reader = query.ExecuteReader())
            while (reader.Read())
                if (TitleNormalizer.Normalize(reader.GetString(1)) == key)
                    evidence.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return new(evidence);
    }

    private SqliteConnection OpenValidated()
    {
        var connection = database.OpenConnection();
        try
        {
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';";
            if ((long)query.ExecuteScalar()! == 0) throw new InvalidOperationException("先に出力用DBを初期化してください。");
            new MigrationRunner().Run(connection);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static void ValidateSource(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (sourceName != sourceName.Trim()) throw new ArgumentException("出典の前後に空白は使用できません。", nameof(sourceName));
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}

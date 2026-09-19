using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Import;

/// <summary>旧11列、またはsong_tag付き12列を同一読取トランザクションから取得する。</summary>
internal static class LegacySourceReader
{
    private static readonly Dictionary<string, string> Columns = new(StringComparer.Ordinal)
    {
        ["id"] = "INTEGER", ["level"] = "TEXT", ["song_name"] = "TEXT", ["difficulty_type"] = "TEXT",
        ["total_notes"] = "INTEGER", ["clear_type"] = "TEXT", ["score"] = "INTEGER",
        ["miss_count"] = "INTEGER", ["played_option"] = "TEXT", ["played_at"] = "TEXT", ["original_data"] = "TEXT"
    };

    internal static LegacySnapshot Read(string path, CancellationToken token)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type FROM sqlite_schema WHERE name='play_history';";
        if (!Equals(command.ExecuteScalar(), "table")) throw new InvalidDataException("旧play_historyテーブルがありません。");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name='schema_migrations';";
        if ((long)command.ExecuteScalar()! != 0) throw new InvalidDataException("新形式DBは旧履歴の入力にできません。");
        command.CommandText = "PRAGMA table_xinfo(play_history);";
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (reader.GetInt32(6) != 0 || reader.GetInt32(5) != (name == "id" ? 1 : 0))
                    throw new InvalidDataException("生成列または未知の主キー構成です。");
                found.Add(name, reader.GetString(2).ToUpperInvariant());
            }
        }
        if (Columns.Any(c => !found.TryGetValue(c.Key, out var type) || type != c.Value)
            || found.Any(c => !Columns.ContainsKey(c.Key) && (c.Key != "song_tag" || c.Value != "TEXT")))
            throw new InvalidDataException("対応していない旧履歴の列構成です。");
        // 列順を固定したJSONを比較するため、物理的な列順・ファイル配置には依存しない。
        var names = found.Keys.Order(StringComparer.Ordinal).ToArray();
        command.CommandText = "SELECT " + string.Join(",", names.Select(n => '"' + n + '"')) + " FROM play_history ORDER BY id;";
        var rows = new List<LegacySourceRow>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < names.Length; i++) row.Add(names[i], reader.IsDBNull(i) ? null : reader.GetValue(i));
                if (row["id"] is not long id) throw new InvalidDataException("元行idが整数ではありません。");
                var raw = LegacyRowConverter.Serialize(row);
                hash.AppendData(Encoding.UTF8.GetBytes(raw + "\n"));
                rows.Add(new(id, row, raw));
            }
        }
        token.ThrowIfCancellationRequested();
        return new(rows, Convert.ToHexString(hash.GetHashAndReset()));
    }
}

internal sealed record LegacySourceRow(long Id, IReadOnlyDictionary<string, object?> Values, string RawData);
internal sealed record LegacySnapshot(IReadOnlyList<LegacySourceRow> Rows, string Fingerprint);

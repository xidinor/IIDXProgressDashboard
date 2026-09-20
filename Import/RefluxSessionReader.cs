using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace IIDXProgressDashboard.Import;

/// <summary>元のヘッダー・行を保持する、DBから独立したSessionの読取結果。</summary>
public sealed record RefluxSessionSnapshot(string Header, IReadOnlyList<string> Columns,
    IReadOnlyList<RefluxSessionRow> Rows, string Fingerprint);

public sealed record RefluxSessionRow(int LineNumber, string RawLine,
    IReadOnlyDictionary<string, string> Values, string? ErrorCode);

/// <summary>Refluxの非引用TSVを読み取る。値の変換やSession同一性の判定は行わない。</summary>
public static class RefluxSessionReader
{
    private static readonly string[] Required = ["title", "difficulty", "lamp", "exscore", "date"];

    public static RefluxSessionSnapshot Read(string path, CancellationToken cancellationToken = default)
    {
        // Windowsでは書込・削除共有を許可せず、読み取り中の追記や置換を防ぐ。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes.Write(buffer, 0, count);
        }
        return Parse(bytes.ToArray(), cancellationToken);
    }

    public static RefluxSessionSnapshot Parse(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        // UTF-16や壊れたUTF-8を置換文字へ丸めない。BOMは先頭のUTF-8 BOMのみ許容する。
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        if (text.Length == 0) throw new InvalidDataException("EMPTY_INPUT: Sessionヘッダーがありません。");
        if (!text.EndsWith('\n'))
            throw new InvalidDataException("INCOMPLETE_INPUT: 末尾改行がありません。書込完了後に再実行してください。");
        if (text.Replace("\r\n", "\n", StringComparison.Ordinal).Contains('\r') || text.Contains('\0'))
            throw new InvalidDataException("INVALID_STRUCTURE: 単独CRまたはNULを含んでいます。");

        var lines = text.Split('\n');
        var header = lines[0].TrimEnd('\r');
        var columns = header.Split('\t');
        if (columns.Any(string.IsNullOrWhiteSpace) || columns.Any(c => c != c.Trim()) ||
            columns.Distinct(StringComparer.Ordinal).Count() != columns.Length)
            throw new InvalidDataException("INVALID_HEADER: 空・前後空白・重複した列名があります。");
        if (Required.Any(c => !columns.Contains(c, StringComparer.Ordinal)))
            throw new InvalidDataException("MISSING_HEADER: title/difficulty/lamp/exscore/dateが必要です。best/trackerは対象外です。");

        var rows = new List<RefluxSessionRow>();
        for (var i = 1; i < lines.Length - 1; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = lines[i].TrimEnd('\r');
            var fields = raw.Split('\t');
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            // 列数不一致の行は対応を推測しない。raw行を残して後段で不正行として保存する。
            if (fields.Length == columns.Length)
                for (var j = 0; j < columns.Length; j++) values.Add(columns[j], fields[j]);
            rows.Add(new(i + 1, raw, new ReadOnlyDictionary<string, string>(values),
                fields.Length == columns.Length ? null : "INVALID_FIELD_COUNT"));
        }
        return new(header, Array.AsReadOnly(columns), rows.AsReadOnly(),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }
}

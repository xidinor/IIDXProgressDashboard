using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace IIDXProgressDashboard.Master;

/// <summary>ローカルのTextage入力を、実行せずに読み込む。構文解析・DB更新は後続工程。</summary>
public sealed class TextageSourceReader
{
    // 旧Pythonの入力一式。CS版を採用する優先順位はこの並びでは決めない。
    public static IReadOnlyList<string> RequiredFileNames { get; } = Array.AsReadOnly(new[]
    {
        "titletbl.js", "scrlist.js", "datatbl.js", "actbl.js", "cstbl.js", "cstbl1.js", "cstbl2.js"
    });

    public async Task<TextageSourceSnapshot> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        cancellationToken.ThrowIfCancellationRequested();
        var missing = RequiredFileNames.Where(name => !File.Exists(Path.Combine(root, name))).ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException($"Textage入力ファイルが不足しています: {string.Join(", ", missing)}");

        var files = new Dictionary<string, TextageSourceFile>(StringComparer.Ordinal);
        foreach (var name in RequiredFileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, name), cancellationToken).ConfigureAwait(false);
            string content;
            try
            {
                // 不正バイトを置換すると曲名照合が変わるため、厳密なUTF-8で拒否する。
                content = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException($"{name}: UTF-8として読み込めません。", ex);
            }
            if (content.StartsWith('\uFEFF')) content = content[1..];
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException($"{name}: 入力が空です。");

            files.Add(name, new TextageSourceFile(name, content, bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes))));
        }

        // 全ファイルが読めた場合だけ返す。読込成功は構文・取得範囲の完全性を保証しない。
        return new TextageSourceSnapshot(new ReadOnlyDictionary<string, TextageSourceFile>(files));
    }
}

public sealed record TextageSourceFile(string Name, string Content, long ByteLength, string Sha256);

public sealed record TextageSourceSnapshot(IReadOnlyDictionary<string, TextageSourceFile> Files);

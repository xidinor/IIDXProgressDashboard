using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace IIDXProgressDashboard.Master;

/// <summary>ローカルのTextage入力を、実行せずに読み込む。構文解析・DB更新は担当しない。</summary>
public sealed class TextageSourceReader
{
    public static IReadOnlyList<string> CatalogFileNames { get; } = Array.AsReadOnly(new[] { "titletbl.js", "scrlist.js", "datatbl.js", "actbl.js" });
    public static IReadOnlyList<string> ComparisonFileNames { get; } = Array.AsReadOnly(new[] { "cstbl.js", "cstbl1.js", "cstbl2.js" });
    // 旧Pythonの入力一式。CS版を採用する優先順位はこの並びでは決めない。
    public static IReadOnlyList<string> RequiredFileNames { get; } = Array.AsReadOnly(new[]
    {
        "titletbl.js", "scrlist.js", "datatbl.js", "actbl.js", "cstbl.js", "cstbl1.js", "cstbl2.js"
    });

    public Task<TextageSourceSnapshot> ReadAsync(string directory, CancellationToken cancellationToken = default)
        => ReadAsync(directory, new TextageReadOptions(IncludeCsComparison: true), cancellationToken);

    public async Task<TextageSourceSnapshot> ReadAsync(string directory, TextageReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(options);
        var root = Path.GetFullPath(directory);
        cancellationToken.ThrowIfCancellationRequested();
        var names = options.IncludeCsComparison ? RequiredFileNames : CatalogFileNames;
        var missing = names.Where(name => !File.Exists(Path.Combine(root, name))).ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException($"Textage入力ファイルが不足しています: {string.Join(", ", missing)}");

        var files = new Dictionary<string, TextageSourceFile>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = DateTimeOffset.UtcNow;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, name), cancellationToken).ConfigureAwait(false);
            files.Add(name, Decode(name, bytes, options.Encoding) with { StartedAt = started, CompletedAt = DateTimeOffset.UtcNow, Location = name });
        }
        // 全入力を読み終えてから再検査し、読込中の変更を成功扱いしない。
        foreach (var name in names)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, name), cancellationToken).ConfigureAwait(false);
            if (Hash(bytes) != files[name].Sha256) throw new InvalidDataException($"{name}: 読込中に入力が変更されました。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        // 全ファイルが読めた場合だけ返す。読込成功は構文・取得範囲の完全性を保証しない。
        return new TextageSourceSnapshot(new ReadOnlyDictionary<string, TextageSourceFile>(files));
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static TextageSourceFile Decode(string name, byte[] bytes, TextageEncoding encoding)
    {
        Encoding decoder;
        if (encoding == TextageEncoding.Utf8) decoder = new UTF8Encoding(false, true);
        else if (encoding == TextageEncoding.Cp932)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            decoder = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        else throw new ArgumentOutOfRangeException(nameof(encoding));
        string content;
        try { content = decoder.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException($"{name}: {encoding}として読み込めません。", ex); }
        if (content.StartsWith('\uFEFF')) content = content[1..];
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException($"{name}: 入力が空です。");
        return new(name, content, bytes.LongLength, Hash(bytes)) { Encoding = encoding };
    }
}

using System.Net;
using System.Text.RegularExpressions;

namespace IIDXProgressDashboard.Master;

/// <summary>Textageの限定データ構文を解析し、全入力が有効なときだけ候補を返す。</summary>
public sealed class TextageMasterParser
{
    private static readonly string[] LongDifficulties = { "BEGINNER", "NORMAL", "HYPER", "ANOTHER", "LEGGENDARIA" };

    public MasterSnapshot Parse(TextageSourceSnapshot sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var diagnostics = new List<MasterDiagnostic>();
        var documents = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (var name in TextageSourceReader.CatalogFileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sources.Files.TryGetValue(name, out var file)) throw new InvalidDataException($"必須ファイル不足: {name}");
            documents[name] = ReadDocument(name, file.Content, cancellationToken);
        }
        var titles = Table(documents["titletbl.js"], "titletbl");
        var levels = Table(documents["actbl.js"], "actbl");
        var notes = Table(documents["datatbl.js"], "datatbl");
        var versionDocument = documents["scrlist.js"];
        if (!versionDocument.TryGetValue("vertbl", out var versionValue) || versionValue is not List<object> versionList)
            throw new InvalidDataException("scrlist.js: vertblがありません。");
        var versions = versionList.Select((v, i) => (i, value: Text(v, "vertbl"))).ToDictionary(x => x.i, x => x.value);
        foreach (var (key, value) in versionDocument.Where(p => p.Key.StartsWith("vertbl[", StringComparison.Ordinal)))
        {
            int index = int.Parse(key[7..^1]);
            if (!versions.TryAdd(index, Text(value, key))) throw new InvalidDataException($"重複version: {index}");
        }

        var songs = new List<MasterSong>();
        var charts = new List<MasterChart>();
        foreach (var (tag, value) in titles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tag == "__dmy__") { diagnostics.Add(new("DUMMY_EXCLUDED", "titletbl.js", tag, "既知のダミー")); continue; }
            var row = Row(value, 6, 7, $"titletbl.js:{tag}");
            int version = Number(row[0], tag);
            _ = Number(row[1], tag); _ = Number(row[2], tag);
            if (!versions.TryGetValue(version, out var versionName)) throw new InvalidDataException($"titletbl.js:{tag}: 未知versionIndex {version}");
            string title = Display(Text(row[5], tag));
            if (row.Count == 7)
            {
                string subtitle = Display(Text(row[6], tag));
                if (subtitle.Length > 0) title += " " + subtitle;
            }
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(title)) throw new InvalidDataException($"titletbl.js:{tag}: 空のtag/title");
            songs.Add(new(tag, title, Display(Text(row[4], tag)), Display(Text(row[3], tag)), versionName, version));
        }
        var tags = songs.Select(x => x.Tag).ToHashSet(StringComparer.Ordinal);
        foreach (var (tag, value) in levels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tags.Contains(tag)) throw new InvalidDataException($"actbl.js:{tag}: 曲情報がありません。");
            var row = LevelRow(value, $"actbl.js:{tag}");
            if (!notes.TryGetValue(tag, out var noteValue)) throw new InvalidDataException($"datatbl.js:{tag}: 必須tag不足");
            var noteRow = NotesRow(noteValue, tag);
            if (Number(row[1], tag) > 0 || Number(noteRow[0], tag) > 0)
                diagnostics.Add(new("SBO_EXCLUDED", "actbl.js", tag, "旧BEGINNERはSP/Bへ統合しない"));
            for (int type = 1; type <= 10; type++)
            {
                int level = Number(row[type * 2 + 1], tag), flags = Number(row[type * 2 + 2], tag), count = Number(noteRow[type], tag);
                if (level == 0 && count == 0)
                {
                    if (flags != 0) throw new InvalidDataException($"actbl.js:{tag}[{type}]: flagsだけが存在します。");
                    continue;
                }
                if ((flags & 2) != 0 && level > 12) throw new InvalidDataException($"actbl.js:{tag}[{type}]: 12段階level範囲外");
                int? normalizedLevel = level > 0 && (flags & 2) != 0 ? level : null;
                if (level > 0 && normalizedLevel is null)
                    diagnostics.Add(new("LEGACY_LEVEL", "actbl.js", tag, $"type={type},level={level},flags={flags}"));
                charts.Add(new(tag, type <= 5 ? "SP" : "DP", MasterDifficulty.FromLegacyName(LongDifficulties[(type - 1) % 5]), normalizedLevel, count > 0 ? count : null));
            }
        }
        // カタログ外のnotesも行構造を確認し、不正行の黙示スキップを防ぐ。
        foreach (var (tag, value) in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NotesRow(value, tag);
            if (!tags.Contains(tag)) throw new InvalidDataException($"datatbl.js:{tag}: 曲情報がありません。");
        }
        var csVersions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in TextageSourceReader.ComparisonFileNames)
        {
            if (!sources.Files.TryGetValue(name, out var file)) continue;
            var cs = ReadDocument(name, file.Content, cancellationToken, documents["actbl.js"]);
            if (!cs.Keys.Any(k => k.StartsWith("cstbl[", StringComparison.Ordinal))) throw new InvalidDataException($"{name}: CS版テーブルがありません。");
            foreach (var (key, value) in cs.Where(x => x.Key.StartsWith("cstbl[", StringComparison.Ordinal)))
            {
                if (!csVersions.Add(key)) throw new InvalidDataException($"{name}: CS版重複 {key}");
                foreach (var (tag, raw) in Table(cs, key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = LevelRow(raw, $"{name}:{key}:{tag}");
                    if (!levels.TryGetValue(tag, out var ac)) diagnostics.Add(new("CS_ONLY", name, tag, key));
                    else if (!row.Take(23).SequenceEqual(((List<object>)ac).Take(23))) diagnostics.Add(new("CS_DIFFERENCE", name, tag, key));
                }
            }
        }
        bool anyCs = TextageSourceReader.ComparisonFileNames.Any(sources.Files.ContainsKey);
        if (anyCs && !TextageSourceReader.ComparisonFileNames.All(sources.Files.ContainsKey)) throw new InvalidDataException("CS比較ファイル一式が不足しています。");
        if (songs.Count == 0 || charts.Count == 0) throw new InvalidDataException("曲・譜面候補が空です。");
        cancellationToken.ThrowIfCancellationRequested();
        return new(songs.AsReadOnly(), charts.AsReadOnly(), diagnostics.AsReadOnly(), sources);
    }

    private static Dictionary<string, object> ReadDocument(string name, string content, CancellationToken cancellation, Dictionary<string, object>? sharedConstants = null)
    {
        var parser = new TextageDataParser(name, content, cancellation);
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        if (sharedConstants is not null)
            foreach (var (key, value) in sharedConstants.Where(p => p.Key is "A" or "B" or "C" or "D" or "E" or "F"))
                parser.Constants.Add(key, value);
        while (parser.Peek().Kind != "eof")
        {
            var token = parser.Take();
            if (token.Kind != "identifier") throw parser.Error("データ代入が必要です。");
            string key = token.Text;
            // 実ファイルの表示部分への入口だけを許可。対象リテラル内には適用しない。
            if ((name == "scrlist.js" && key == "referstr") || (name == "datatbl.js" && key == "function"))
            { parser.SkipDisplayCode(); break; }
            bool allowed = name switch
            {
                "titletbl.js" => key is "titletbl" or "SS" or "VERINDEX" or "IDINDEX" or "OPTINDEX" or "GENREINDEX" or "ARTISTINDEX" or "TITLEINDEX" or "SUBTITLEINDEX",
                "actbl.js" => key is "actbl" or "pspver" or "A" or "B" or "C" or "D" or "E" or "F" or "e_list" or "s_list" or "s_list_cc" or "d_list" or "d_list_cc",
                "datatbl.js" => key == "datatbl",
                "scrlist.js" => key == "vertbl",
                _ => key is "cstbl" or "cs_elist" or "cs_slist" or "cs_dlist"
            };
            if (!allowed) throw parser.Error($"未対応の代入: {key}");
            if (parser.Match("["))
            {
                var index = parser.Value();
                key += $"[{Number(index, name)}]";
                parser.Expect("]");
            }
            parser.Expect("=");
            object value;
            if (parser.Peek().Text == "new" && key is "cstbl" or "cs_elist" or "cs_slist" or "cs_dlist")
            {
                parser.Take();
                if (parser.Take().Text != "Array") throw parser.Error("未対応のnew式");
                parser.Expect("("); parser.Expect(")"); value = new List<object>();
            }
            else value = parser.Value();
            if (!data.TryAdd(key, value)) throw parser.Error($"重複代入: {key}");
            if (key is "A" or "B" or "C" or "D" or "E" or "F" or "SS")
            {
                if (value is not int) throw parser.Error("定数値が不正です。");
                if (key != "SS" && (int)value != key[0] - 'A' + 10) throw parser.Error("A〜F定数の定義が変更されています。");
                parser.Constants[key] = value;
            }
            var columnConstants = new[] { "VERINDEX", "IDINDEX", "OPTINDEX", "GENREINDEX", "ARTISTINDEX", "TITLEINDEX", "SUBTITLEINDEX" };
            int expected = Array.IndexOf(columnConstants, key);
            if (expected >= 0 && !Equals(value, expected)) throw parser.Error($"列定義が変更されています: {key}");
            if (parser.Match(","))
            {
                if (key is not ("A" or "B" or "C" or "D" or "E" or "F")) throw parser.Error("未対応の連続代入です。");
                if (parser.Peek().Kind == "eof") throw parser.Error("連続代入が途中で終了しました。");
            }
            else parser.Expect(";");
        }
        return data;
    }

    private static Dictionary<string, object> Table(Dictionary<string, object> document, string key)
        => document.TryGetValue(key, out var value) && value is Dictionary<string, object> table ? table : throw new InvalidDataException($"{key}: 必須object不足");
    private static List<object> Row(object value, int min, int max, string context)
        => value is List<object> row && row.Count >= min && row.Count <= max ? row : throw new InvalidDataException($"{context}: 配列長・型が不正");
    private static List<object> LevelRow(object value, string context)
    {
        var row = Row(value, 23, 24, context);
        foreach (var cell in row.Take(23)) Number(cell, context);
        for (int type = 0; type <= 10; type++)
        {
            int flags = (int)row[type * 2 + 2];
            if (flags > 15) throw new InvalidDataException($"{context}: 未対応のflags {flags}");
            if ((flags & 2) != 0 && (int)row[type * 2 + 1] > 12) throw new InvalidDataException($"{context}: 12段階level範囲外");
        }
        if (row.Count == 24) Text(row[23], context);
        return row;
    }
    private static List<object> NotesRow(object value, string context)
    {
        var row = Row(value, 12, 12, context);
        foreach (var cell in row.Take(11)) Number(cell, context);
        Text(row[11], context);
        return row;
    }
    private static int Number(object value, string context) => value is int n && n >= 0 ? n : throw new InvalidDataException($"{context}: 非負整数が必要");
    private static string Text(object value, string context) => value is string s ? s : throw new InvalidDataException($"{context}: 文字列が必要");
    private static string Display(string text) => WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).Trim();
}

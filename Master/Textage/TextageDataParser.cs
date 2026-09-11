using System.Globalization;
using System.Text;

namespace IIDXProgressDashboard.Master;

/// <summary>JSエンジンを使わず、データ代入に必要な字句と値だけを読む。</summary>
internal sealed class TextageDataParser
{
    internal sealed record Token(string Kind, string Text, int Offset);
    private readonly string source;
    private readonly string file;
    private readonly CancellationToken cancellation;
    private int offset;
    private Token? pending;
    internal Dictionary<string, object> Constants { get; } = new(StringComparer.Ordinal);

    internal TextageDataParser(string file, string source, CancellationToken cancellation)
        => (this.file, this.source, this.cancellation) = (file, source, cancellation);

    internal InvalidDataException Error(string message)
        => new($"{file}@{pending?.Offset ?? offset}: {message}");
    internal Token Peek() => pending ??= Lex();
    internal Token Take() { var token = Peek(); pending = null; return token; }
    internal bool Match(string text)
    {
        if (Peek().Kind == "symbol" && Peek().Text == text) { Take(); return true; }
        return false;
    }
    internal void Expect(string text)
    {
        if (!Match(text)) throw Error($"'{text}' が必要です。");
    }

    private Token Lex()
    {
        while (offset < source.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            if (char.IsWhiteSpace(source[offset])) { offset++; continue; }
            if (source[offset] == '/' && offset + 1 < source.Length)
            {
                if (source[offset + 1] == '/')
                {
                    offset += 2;
                    while (offset < source.Length && source[offset] is not '\r' and not '\n') offset++;
                    continue;
                }
                if (source[offset + 1] == '*')
                {
                    var end = source.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                    if (end < 0) throw Error("コメントが閉じていません。");
                    offset = end + 2;
                    continue;
                }
            }
            break;
        }
        var start = offset;
        if (offset == source.Length) return new("eof", "", offset);
        char c = source[offset++];
        if (c is '\'' or '"')
        {
            var value = new StringBuilder();
            while (offset < source.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                char next = source[offset++];
                if (next == c) return new("string", value.ToString(), start);
                if (next is '\r' or '\n') throw Error("文字列中の未エスケープ改行です。");
                if (next != '\\') { value.Append(next); continue; }
                if (offset == source.Length) throw Error("escapeが途中で終了しました。");
                next = source[offset++];
                switch (next)
                {
                    case '\\': case '\'': case '"': case '/': value.Append(next); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'v': value.Append('\v'); break;
                    case '\n': break;
                    case '\r': if (offset < source.Length && source[offset] == '\n') offset++; break;
                    case 'u': case 'x':
                        int count = next == 'u' ? 4 : 2;
                        if (offset + count > source.Length || !int.TryParse(source.AsSpan(offset, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            throw Error("不正な文字escapeです。");
                        value.Append((char)code); offset += count; break;
                    default: throw Error($"未対応のescape: {next}");
                }
            }
            throw Error("文字列が閉じていません。");
        }
        if (char.IsAsciiDigit(c))
        {
            while (offset < source.Length && char.IsAsciiDigit(source[offset])) offset++;
            return new("number", source[start..offset], start);
        }
        if (char.IsAsciiLetter(c) || c is '_' or '$')
        {
            while (offset < source.Length && (char.IsAsciiLetterOrDigit(source[offset]) || source[offset] is '_' or '$')) offset++;
            return new("identifier", source[start..offset], start);
        }
        if ("{}[]();,:.=+-*/<>!&|?%~^".Contains(c)) return new("symbol", c.ToString(), start);
        throw Error($"未対応token: {c}");
    }

    internal object Value(int depth = 0)
    {
        if (depth > 64) throw Error("入れ子が深すぎます。");
        if (Match("["))
        {
            var values = new List<object>();
            if (Match("]")) return values;
            do { values.Add(Value(depth + 1)); if (Match("]")) return values; Expect(","); } while (!Match("]"));
            return values;
        }
        if (Match("{"))
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            if (Match("}")) return values;
            do
            {
                var key = Take();
                if (key.Kind is not "string" and not "identifier") throw Error("objectのキーが不正です。");
                Expect(":");
                if (!values.TryAdd(key.Text, Value(depth + 1))) throw Error($"重複tag: {key.Text}");
                if (Match("}")) return values;
                Expect(",");
            } while (!Match("}"));
            return values;
        }
        bool negative = Match("-");
        var token = Take();
        if (token.Kind == "number" && int.TryParse((negative ? "-" : "") + token.Text, out int number)) return number;
        if (negative) throw Error("数値が必要です。");
        object result;
        if (token.Kind == "string") result = token.Text;
        else if (token.Kind == "identifier" && Constants.TryGetValue(token.Text, out var constant)) result = constant;
        else throw Error($"未対応の値: {token.Text}");
        // fontcolorは文字列装飾として構文だけ検証する。呼出しは一切実行しない。
        if (result is string && Match("."))
        {
            var method = Take();
            if (method.Kind != "identifier" || method.Text != "fontcolor") throw Error("未対応の文字列装飾です。");
            Expect("(");
            if (Take().Kind != "string") throw Error("fontcolorの引数が不正です。");
            Expect(")");
        }
        return result;
    }

    // データ対象外の表示コードも、文字列・コメント・括弧・EOFを検査する。
    // これはJSの実行・完全な文法検証ではない。対象データの再代入は拒否する。
    internal void SkipDisplayCode()
    {
        var stack = new Stack<string>();
        string previous = "";
        while (Peek().Kind != "eof")
        {
            var token = Take();
            if (token.Kind == "symbol" && token.Text == "/" && previous is "(" or "=" or "," or ":")
            {
                // 表示コードの正規表現リテラル。文字クラス中の / や括弧を境界にしない。
                bool characterClass = false, closed = false;
                while (offset < source.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    char c = source[offset++];
                    if (c is '\r' or '\n') throw Error("正規表現が閉じていません。");
                    if (c == '\\')
                    {
                        if (offset >= source.Length || source[offset] is '\r' or '\n') throw Error("不正な正規表現escape");
                        offset++; continue;
                    }
                    if (c == '[') characterClass = true;
                    if (c == ']') characterClass = false;
                    if (c == '/' && !characterClass) { closed = true; break; }
                }
                if (!closed) throw Error("正規表現が途中で終了しました。");
                while (offset < source.Length && char.IsAsciiLetter(source[offset])) offset++;
                previous = "regex";
                continue;
            }
            if (token.Kind == "identifier" && token.Text is "titletbl" or "actbl" or "datatbl" or "vertbl" or "cstbl")
            {
                RejectDataWrite();
            }
            previous = token.Kind == "symbol" ? token.Text : token.Kind;
            if (token.Kind != "symbol") continue;
            if (token.Text is "(" or "[" or "{") stack.Push(token.Text);
            if (token.Text is ")" or "]" or "}")
            {
                if (stack.Count == 0 || stack.Pop() != (token.Text == ")" ? "(" : token.Text == "]" ? "[" : "{"))
                    throw Error("表示コードの括弧が不整合です。");
            }
        }
        if (stack.Count != 0) throw Error("表示コードが途中で終了しました。");
    }

    private void RejectDataWrite()
    {
        // 添字・プロパティ経由の代入も調べる。参照だった場合は字句位置を戻す。
        int savedOffset = offset;
        var savedPending = pending;
        while (true)
        {
            if (Match("["))
            {
                int nesting = 1;
                while (nesting > 0)
                {
                    var token = Take();
                    if (token.Kind == "eof") throw Error("添字が閉じていません。");
                    if (token.Kind == "symbol" && token.Text == "[") nesting++;
                    if (token.Kind == "symbol" && token.Text == "]") nesting--;
                }
            }
            else if (Match("."))
            {
                if (Take().Kind != "identifier") throw Error("プロパティ名が不正です。");
            }
            else break;
        }
        bool write = false;
        if (Match("=")) write = !Match("="); // == / === は比較。
        else if (Match("+")) write = Match("+") || Match("=");
        else if (Match("-")) write = Match("-") || Match("=");
        else if (Peek().Text is "*" or "/" or "&" or "|" or "^" or "%") { Take(); write = Match("="); }
        if (write) throw Error("表示コード中のマスター再代入は未対応です。");
        offset = savedOffset;
        pending = savedPending;
    }
}

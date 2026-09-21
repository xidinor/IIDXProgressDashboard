using System.Globalization;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace IIDXProgressDashboard.Master;

/// <summary>全文の文法はAcornimaへ委譲し、Textageで許可したデータだけを取り出す。JSは実行しない。</summary>
internal sealed class TextageAstReader(string file, string source, CancellationToken cancellation)
{
    // 巨大入力はAST生成前に拒否する。2026-09-21の実入力は各ファイル約220KB以下。
    internal const int MaxSourceLength = 8 * 1024 * 1024;
    private readonly Dictionary<string, object> constants = new(StringComparer.Ordinal);
    private static readonly string[] Columns = ["VERINDEX", "IDINDEX", "OPTINDEX", "GENREINDEX", "ARTISTINDEX", "TITLEINDEX", "SUBTITLEINDEX"];
    private static readonly Regex DecimalInteger = new("^[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex DataIdentifier = new("\\A[A-Za-z_$][A-Za-z0-9_$]*\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    // 復号はAcornimaが行う。この規則は従来許可していなかったescapeの受理を防ぐ入力契約。
    private static readonly Regex LegacyString = new("\\A(?:[^\\\\]|\\\\(?:[\\\\'\"/nrtbfv]|u[0-9a-fA-F]{4}|x[0-9a-fA-F]{2}|\\r\\n|\\r|\\n))*\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    internal Dictionary<string, object> Read(Dictionary<string, object>? sharedConstants = null)
    {
        try { return ReadCore(sharedConstants); }
        catch (RegexMatchTimeoutException ex)
        {
            throw new InvalidDataException($"{file}: リテラルの検証時間上限を超えています。", ex);
        }
    }

    private Dictionary<string, object> ReadCore(Dictionary<string, object>? sharedConstants)
    {
        cancellation.ThrowIfCancellationRequested();
        if (source.Length > MaxSourceLength) throw Error(null, "入力サイズ上限を超えています。");
        if (sharedConstants is not null)
            foreach (var (key, value) in sharedConstants.Where(p => IsHexConstant(p.Key))) constants.Add(key, value);

        Script script;
        try
        {
            script = new Parser(new ParserOptions
            {
                Tolerant = false,
                EcmaVersion = EcmaVersion.ES2022,
                PreserveParens = true,
                OnToken = (in Token token) => cancellation.ThrowIfCancellationRequested(),
                OnComment = (in Comment comment) => cancellation.ThrowIfCancellationRequested(),
                // 構文解析中もノード生成単位で中止を確認。単一の長い文字列・コメント中は割り込めない。
                OnNode = (Node node, in OnNodeContext context) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    int depth = 1;
                    foreach (var child in node.ChildNodes) depth = Math.Max(depth, TreeDepth(child) + 1);
                    if (depth > 128) throw Error(node, "構文木が深すぎます。");
                    node.UserData = depth;
                }
            }).ParseScript(source, file);
        }
        catch (ParseErrorException ex)
        {
            throw new InvalidDataException($"{file}:{ex.Error.LineNumber}:{ex.Error.Column}: JavaScript構文エラー: {ex.Error.Description}", ex);
        }
        catch (InsufficientExecutionStackException ex)
        {
            // Acornimaのスタック保護で中断した入力も、部分候補を返さず全体失敗とする。
            throw new InvalidDataException($"{file}: 構文の入れ子が深すぎます。", ex);
        }
        cancellation.ThrowIfCancellationRequested();

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        bool display = false;
        foreach (var statement in script.Body)
        {
            cancellation.ThrowIfCancellationRequested();
            display |= IsDisplayStart(statement);
            if (display) { CheckDisplay(statement); continue; }
            if (statement is not ExpressionStatement expression) throw Error(statement, "データ代入が必要です。");
            // ASIで補われたセミコロンは従来の切断入力との境界を変えるため受理しない。
            if (source[statement.Range.End - 1] != ';') throw Error(statement, "明示的なセミコロンが必要です。");
            if (expression.Expression is SequenceExpression sequence)
            {
                for (int i = 0; i < sequence.Expressions.Count; i++)
                    ReadAssignment(sequence.Expressions[i], data, i < sequence.Expressions.Count - 1);
            }
            else ReadAssignment(expression.Expression, data, false);
        }
        return data;
    }

    private void ReadAssignment(Expression expression, Dictionary<string, object> data, bool followedByComma)
    {
        // JSとして有効なUnicode識別子・識別子escapeは、従来の入力契約へ追加しない。
        var nodes = new Stack<Node>();
        nodes.Push(expression);
        while (nodes.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested();
            if (node is Identifier id && (!DataIdentifier.IsMatch(id.Name) || source[id.Start..id.End] != id.Name))
                throw Error(id, "未対応のデータ識別子です。");
            foreach (var child in node.ChildNodes) nodes.Push(child);
        }
        if (expression is not AssignmentExpression assignment || assignment.Operator != Operator.Assignment)
            throw Error(expression, "単純代入が必要です。");
        string key;
        if (assignment.Left is Identifier identifier) key = identifier.Name;
        else if (assignment.Left is MemberExpression { Computed: true, Object: Identifier owner } member)
        {
            var index = Value(member.Property);
            if (index is not int n || n < 0) throw Error(member, "非負整数の添字が必要です。");
            key = $"{owner.Name}[{n.ToString(CultureInfo.InvariantCulture)}]";
        }
        else throw Error(assignment.Left, "未対応の代入先です。");
        string root = key.Split('[')[0];
        bool allowed = file switch
        {
            "titletbl.js" => root is "titletbl" or "SS" || Columns.Contains(root),
            "actbl.js" => root is "actbl" or "pspver" or "e_list" or "s_list" or "s_list_cc" or "d_list" or "d_list_cc" || IsHexConstant(root),
            "datatbl.js" => root == "datatbl",
            "scrlist.js" => root == "vertbl",
            _ => root is "cstbl" or "cs_elist" or "cs_slist" or "cs_dlist"
        };
        if (!allowed) throw Error(assignment, $"未対応の代入: {key}");
        if (followedByComma && !IsHexConstant(key)) throw Error(assignment, "未対応の連続代入です。");
        object value;
        if (assignment.Right is NewExpression { Callee: Identifier { Name: "Array" }, Arguments.Count: 0 } creation
            && source[creation.End - 1] == ')'
            && key is "cstbl" or "cs_elist" or "cs_slist" or "cs_dlist") value = new List<object>();
        else value = Value(assignment.Right);
        if (!data.TryAdd(key, value)) throw Error(assignment, $"重複代入: {key}");
        if (IsHexConstant(key) || key == "SS")
        {
            if (value is not int number || (key != "SS" && number != key[0] - 'A' + 10))
                throw Error(assignment, "定数定義が不正です。");
            constants[key] = value;
        }
        int expected = Array.IndexOf(Columns, key);
        if (expected >= 0 && !Equals(value, expected)) throw Error(assignment, $"列定義が変更されています: {key}");
    }

    private object Value(Expression expression, int depth = 0)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth > 64) throw Error(expression, "データの入れ子が深すぎます。");
        switch (expression)
        {
            case ArrayExpression array:
                var items = new List<object>();
                foreach (var item in array.Elements)
                {
                    if (item is null) throw Error(array, "配列の省略要素は未対応です。");
                    items.Add(Value(item, depth + 1));
                }
                return items;
            case ObjectExpression obj:
                var values = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var item in obj.Properties)
                {
                    if (item is not ObjectProperty { Computed: false, Method: false, Shorthand: false, Kind: PropertyKind.Init } property)
                        throw Error(item, "未対応のobjectプロパティです。");
                    string key = property.Key switch
                    {
                        Identifier id => id.Name,
                        Literal { Value: string } text => StringValue(text),
                        _ => throw Error(property, "objectキーは文字列か識別子が必要です。")
                    };
                    if (!values.TryAdd(key, Value((Expression)property.Value, depth + 1))) throw Error(property, $"重複tag: {key}");
                }
                return values;
            case Literal { Value: string } literal: return StringValue(literal);
            case Literal { Value: double } literal: return IntegerValue(literal, false);
            case UnaryExpression { Operator: Operator.UnaryNegation, Argument: Literal { Value: double } number }:
                return IntegerValue(number, true);
            case Identifier id when constants.TryGetValue(id.Name, out var value): return value;
            case CallExpression { Optional: false, Callee: MemberExpression { Computed: false, Optional: false, Property: Identifier { Name: "fontcolor" } } member, Arguments.Count: 1 } call
                when member.Object is Literal { Value: string } && call.Arguments[0] is Literal { Value: string }:
                // 装飾の構造と引数のみ検証し、関数は呼び出さず元の文字列を返す。
                _ = StringValue((Literal)call.Arguments[0]);
                return StringValue((Literal)member.Object);
            default: throw Error(expression, $"未対応の値: {expression.Type}");
        }
    }

    private int IntegerValue(Literal literal, bool negative)
    {
        if (!DecimalInteger.IsMatch(literal.Raw) || (literal.Raw.Length > 1 && literal.Raw[0] == '0')
            || !int.TryParse((negative ? "-" : "") + literal.Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
            throw Error(literal, "10進整数が必要です（先頭ゼロ不可）。");
        return value;
    }

    private string StringValue(Literal literal)
    {
        if (!LegacyString.IsMatch(literal.Raw[1..^1])) throw Error(literal, "未対応の文字escapeです。");
        return (string)literal.Value!;
    }

    private bool IsDisplayStart(Node node)
        => file == "datatbl.js" && node is FunctionDeclaration
        || file == "scrlist.js" && node is ExpressionStatement { Expression: AssignmentExpression { Left: Identifier { Name: "referstr" } } };

    private void CheckDisplay(Node root)
    {
        // 文法検証済みASTで直接の書換えを調べる。alias経由のデータフロー解析は行わない。
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested();
            Node? target = node switch
            {
                AssignmentExpression assignment => assignment.Left,
                UpdateExpression update => update.Argument,
                UnaryExpression { Operator: Operator.Delete } delete => delete.Argument,
                VariableDeclarator declaration => declaration.Id,
                ForInStatement loop => loop.Left,
                ForOfStatement loop => loop.Left,
                _ => null
            };
            if (target is not null && ContainsProtectedTarget(target)) throw Error(node, "表示コード中のマスター書換えは未対応です。");
            foreach (var child in node.ChildNodes) stack.Push(child);
        }
    }

    private static bool ContainsProtectedTarget(Node target)
    {
        if (target is Identifier id) return id.Name is "titletbl" or "actbl" or "datatbl" or "vertbl" or "cstbl";
        if (target is MemberExpression member) return ContainsProtectedTarget(member.Object);
        foreach (var child in target.ChildNodes) if (ContainsProtectedTarget(child)) return true;
        return false;
    }

    private static bool IsHexConstant(string key) => key is "A" or "B" or "C" or "D" or "E" or "F";
    private int TreeDepth(Node node)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node.UserData is int known) return known;
        // Acornimaが配列式を分割代入patternへ変換したノードにはコールバックが来ない。
        // その場合だけ子から深さを回収し、通常のノードと同じ上限を適用する。
        var pending = new Stack<(Node Node, int Depth)>();
        pending.Push((node, 1));
        int result = 1;
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            int depth = item.Depth + (item.Node.UserData is int cached ? cached - 1 : 0);
            if (depth > 128) throw Error(item.Node, "構文木が深すぎます。");
            result = Math.Max(result, depth);
            if (item.Node.UserData is int) continue;
            foreach (var child in item.Node.ChildNodes) pending.Push((child, item.Depth + 1));
        }
        return result;
    }
    private InvalidDataException Error(Node? node, string message)
        => new($"{file}:{node?.Location.Start.Line ?? 1}:{(node?.Location.Start.Column ?? 0) + 1}: {message}");
}

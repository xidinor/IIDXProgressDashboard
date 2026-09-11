using IIDXProgressDashboard.Database;
using Microsoft.Data.Sqlite;

namespace IIDXProgressDashboard.Matching;

/// <summary>過去履歴用の照合。非アクティブも含め、一意性と入力の整合性が揃う場合だけIDを返す。</summary>
public sealed class ChartResolver
{
    private readonly DatabaseInitializer database;

    public ChartResolver(DatabaseInitializer database) =>
        this.database = database ?? throw new ArgumentNullException(nameof(database));

    public ChartResolution Resolve(ChartResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<ChartResolutionIssue>();
        var candidates = new List<ChartCandidate>();
        var evidence = new List<SongTitleEvidence>();
        ChartResolution Result(long? id = null) => new(request, id, issues, candidates, evidence);
        ChartResolution Fail(string code, string detail)
        {
            issues.Add(new(code, detail));
            return Result();
        }

        if (!TryParseChart(request.Difficulty, request.PlayStyle, out var style, out var difficulty))
            return Fail("INVALID_DIFFICULTY", "SP/DPとB/N/H/A/Lを一意に解釈できません。");
        if (request.Level is < 1 or > 12) issues.Add(new("INVALID_LEVEL", "levelは1～12またはnullで指定してください。"));
        if (request.TotalNotes is < 0) issues.Add(new("INVALID_NOTES", "total_notesは0以上またはnullで指定してください。"));
        if (request.SourceName is not null && (string.IsNullOrWhiteSpace(request.SourceName) || request.SourceName != request.SourceName.Trim()))
            issues.Add(new("INVALID_SOURCE", "出典は空・前後空白を許可しません。"));
        if (request.Tag is not null && string.IsNullOrWhiteSpace(request.Tag))
            issues.Add(new("INVALID_TAG", "指定tagが空です。"));
        if (request.Title is null && request.Tag is null)
            issues.Add(new("INVALID_TITLE", "タイトルまたはtagが必要です。"));
        if (request.Title is not null)
        {
            try
            {
                if (TitleNormalizer.Normalize(request.Title).Length == 0)
                    issues.Add(new("INVALID_TITLE", "指定タイトルが空です。"));
            }
            catch (ArgumentException) { issues.Add(new("INVALID_TITLE", "タイトルのUnicodeが不正です。")); }
        }
        if (issues.Count > 0) return Result();

        using var connection = database.OpenConnection();
        // 空DBを初期化しない。既存v1を検証してから同じsnapshotで候補と譜面を読む。
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';";
            if ((long)check.ExecuteScalar()! == 0) throw new InvalidOperationException("先に出力用DBを初期化してください。");
        }
        new MigrationRunner().Run(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        var tags = new List<string>();
        if (request.Title is not null)
        {
            var match = SongAliasRepository.FindCandidates(connection, transaction, request.Title, request.SourceName);
            evidence.AddRange(match.Evidence);
            tags.AddRange(match.Tags);
        }
        // 候補情報は曖昧・矛盾時にも返す。level/notesや活動状態で候補を間引かない。
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT c.chart_id,c.tag,s.title,c.play_style,c.difficulty,c.level,c.total_notes,s.is_active,c.is_active
                FROM charts c JOIN songs s ON s.tag=c.tag
                WHERE c.play_style=$style AND c.difficulty=$difficulty ORDER BY c.tag,c.chart_id;
                """;
            query.Parameters.AddWithValue("$style", style);
            query.Parameters.AddWithValue("$difficulty", difficulty);
            using var reader = query.ExecuteReader();
            while (reader.Read())
                if (tags.Contains(reader.GetString(1), StringComparer.Ordinal) || request.Tag == reader.GetString(1))
                    candidates.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.GetInt64(7) == 1, reader.GetInt64(8) == 1));
        }
        string tag;
        if (request.Tag is not null)
        {
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT 1 FROM songs WHERE tag=$tag;";
            query.Parameters.AddWithValue("$tag", request.Tag);
            if (query.ExecuteScalar() is null) return Fail("TAG_NOT_FOUND", "指定tagが存在しません。");
            if (request.Title is not null && !tags.Contains(request.Tag, StringComparer.Ordinal))
                return Fail("TAG_TITLE_MISMATCH", "指定tagはタイトル・対象aliasの候補に含まれません。");
            tag = request.Tag;
        }
        else
        {
            if (tags.Count == 0) return Fail("SONG_NOT_FOUND", "タイトル・対象aliasに一致する曲がありません。");
            if (tags.Count > 1) return Fail("AMBIGUOUS_SONG", "複数tagに一致するため自動決定できません。");
            tag = tags[0];
        }
        var chart = candidates.SingleOrDefault(c => c.Tag == tag);
        if (chart is null) return Fail("CHART_NOT_FOUND", "確定したtagに指定譜面が存在しません。");
        if (request.Level.HasValue && chart.Level.HasValue && request.Level != chart.Level)
            issues.Add(new("LEVEL_MISMATCH", $"入力level={request.Level}、マスターlevel={chart.Level}。"));
        if (request.TotalNotes.HasValue && chart.TotalNotes.HasValue && request.TotalNotes != chart.TotalNotes)
            issues.Add(new("NOTES_MISMATCH", $"入力notes={request.TotalNotes}、マスターnotes={chart.TotalNotes}。"));
        return Result(issues.Count == 0 ? chart.ChartId : null);
    }

    private static bool TryParseChart(string? value, string? suppliedStyle, out string style, out string difficulty)
    {
        style = suppliedStyle ?? "";
        difficulty = value ?? "";
        // 外部表記を列挙した範囲でのみ変換する。SB/SBo等のProvider固有slotは受理しない。
        if (difficulty.Length == 3 && (difficulty.StartsWith("SP", StringComparison.Ordinal) || difficulty.StartsWith("DP", StringComparison.Ordinal)))
        {
            var embeddedStyle = difficulty[..2];
            if (suppliedStyle is not null && suppliedStyle != embeddedStyle) return false;
            style = embeddedStyle;
            difficulty = difficulty[2..];
        }
        difficulty = difficulty switch
        {
            "BEGINNER" => "B", "NORMAL" => "N", "HYPER" => "H",
            "ANOTHER" => "A", "LEGGENDARIA" => "L", _ => difficulty
        };
        return style is "SP" or "DP" && difficulty is "B" or "N" or "H" or "A" or "L";
    }
}

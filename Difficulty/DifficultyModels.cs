using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IIDXProgressDashboard.Difficulty;

public enum DifficultyTableKind { Sp11Normal, Sp11Hard, Sp12Normal, Sp12Hard }
public sealed record DifficultyRank(string Code, string OriginalName, string DisplayName, string Kind, int SortOrder);
public sealed record DifficultyTableDefinition(DifficultyTableKind Kind, string Code, string DisplayName,
    int Level, string Gauge, string SourceName, string Url, IReadOnlyList<DifficultyRank> Ranks)
{
    public string PlayStyle => "SP";
    public static DifficultyTableDefinition Get(DifficultyTableKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        bool eleven = kind is DifficultyTableKind.Sp11Normal or DifficultyTableKind.Sp11Hard;
        bool hard = kind is DifficultyTableKind.Sp11Hard or DifficultyTableKind.Sp12Hard;
        string gauge = hard ? "HARD" : "NORMAL", source = eleven ? "ATWIKI_BEMANI2SP11" : "IIDX_SP12_GITHUB";
        int level = eleven ? 11 : 12;
        var ranks = new List<DifficultyRank>();
        string[] grades = ["F", "E", "D", "C", "B", "B+", "A", "A+", "S", "S+"];
        for (int i = 0; i < grades.Length; i++)
        {
            if (eleven && grades[i] is "A+" or "B+") continue;
            string suffix = grades[i].Replace("+", "_PLUS");
            ranks.Add(new("JIRIKI_" + suffix, "地力" + grades[i], "地力" + grades[i], "JIRIKI", (i + 1) * 10));
            if (!eleven || (i != 0 && !(hard && i == 1)))
                ranks.Add(new("KOJINSA_" + suffix, "個人差" + grades[i], "個人差" + grades[i], "KOJINSA", (i + 1) * 10 - 5));
        }
        ranks.Add(eleven ? new("UNDECIDED", "未定", "未定", "UNDECIDED", -10)
            : new("UNRATED", "", "評価未割当", "UNDECIDED", -20));
        if (eleven && hard) ranks.Add(new("EXTREME_KOJINSA", "超個人差", "超個人差", "SPECIAL", 110));
        return new(kind, $"{source}_SP{level}_{gauge}", $"SP☆{level} {gauge}（{(eleven ? "Wiki" : "iidx-sp12")}）",
            level, gauge, source, eleven ? $"https://w.atwiki.jp/bemani2sp11/pages/{(hard ? 21 : 22)}.html"
            : "https://iidx-sp12.github.io/songs.json", ranks.AsReadOnly());
    }
}

/// <summary>HTTP原本と採取DOMを区別する。Cookie・認証情報は保存しない。</summary>
public sealed record DifficultySource(string RequestedUrl, string FinalUrl, string InputKind, string Content,
    string Sha256, int ByteLength, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    int? HttpStatus = null, string? ContentType = null, string? ETag = null, string? LastModified = null,
    string? SourceRevision = null)
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public static DifficultySource FromUtf8(string url, byte[] bytes, string inputKind = "HTTP_BODY")
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("入力上限8 MiBを超えています。");
        if (inputKind is not ("HTTP_BODY" or "BROWSER_DOM")) throw new ArgumentException("未知の入力種別です。", nameof(inputKind));
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var now = DateTimeOffset.UtcNow;
        return new(url, url, inputKind, text, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, now, now);
    }
}
public sealed record DifficultyDiagnostic(string Code, string Detail, int? RowOrdinal = null);
public sealed record DifficultyCandidate(string Title, string? Tag, string Difficulty, int? Notes, string RankCode)
{
    public string PlayStyle => "SP";
}
public sealed record DifficultySourceRow(int Ordinal, string Raw, string? Heading, string? OriginalRank,
    string? OriginalTitle, string? SourceKey, DifficultyCandidate? Candidate, string RecordKey);
public sealed record DifficultyParseResult(DifficultyTableDefinition Table, DifficultySource? Source,
    IReadOnlyList<DifficultySourceRow> Rows, IReadOnlyList<DifficultyDiagnostic> Diagnostics,
    string AcquisitionStatus, string ParseStatus, bool ReadComplete)
{
    public const int ParserVersion = 1;
    public const int ContractVersion = 1;
    public bool IsValid => AcquisitionStatus == "COMPLETE" && ParseStatus == "VALID";
    internal static string RowKey(DifficultyTableDefinition table, DifficultySource source, int ordinal)
        => "dt:v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new object[] { table.Code, source.InputKind, source.Sha256, ordinal }))));
}

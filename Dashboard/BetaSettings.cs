using System.Text;

namespace IIDXProgressDashboard.Dashboard;

/// <summary>本体横のUTF-8 INI。既存INIの自動修復・自動上書きはしない。</summary>
public sealed record BetaSettings(string Directory, string DatabaseFile, string LegacyFile, Guid SourceId, bool ExcludeMissingBp)
{
    public const string FileName = "IIDXProgressDashboard.ini";
    public string DatabasePath => Path.Combine(Directory, DatabaseFile);
    public string LegacyPath => Path.Combine(Directory, LegacyFile);

    public static BetaSettings Read(string directory)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool section = false;
        foreach (var line in File.ReadAllLines(Path.Combine(directory, FileName), new UTF8Encoding(false, true)))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith(';')) continue;
            if (text == "[Beta]") { section = true; continue; }
            var split = text.Split('=', 2);
            if (!section || split.Length != 2 || !values.TryAdd(split[0].Trim(), split[1].Trim()))
                throw new InvalidDataException("INIの形式・セクション・重複キーを確認してください。");
        }
        if (values.Count != 4 || !values.TryGetValue("Database", out var database) || !values.TryGetValue("Legacy", out var legacy)
            || !values.TryGetValue("SourceId", out var id) || !Guid.TryParse(id, out var guid) || guid == Guid.Empty
            || !values.TryGetValue("ExcludeMissingBp", out var exclude) || !bool.TryParse(exclude, out var flag))
            throw new InvalidDataException("INIのDatabase / Legacy / SourceId / ExcludeMissingBpを確認してください。");
        ValidateName(database); ValidateName(legacy);
        if (database.Equals(legacy, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("入力と出力のDB名は分けてください。");
        return new(Path.GetFullPath(directory), database, legacy, guid, flag);
    }

    public void Create()
    {
        ValidateName(DatabaseFile); ValidateName(LegacyFile);
        // CreateNewと排他共有で同時実行を拒否。GUIDを確実に保存してからImporterへ渡す。
        using var file = new FileStream(Path.Combine(Directory, FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true);
        writer.Write($"[Beta]\nDatabase={DatabaseFile}\nLegacy={LegacyFile}\nSourceId={SourceId:D}\nExcludeMissingBp={ExcludeMissingBp}\n");
        writer.Flush(); file.Flush(true);
    }

    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.GetFileName(value) != value || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value is "." or ".." || !value.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ベータ版のDBは本体横の.dbファイル名で指定してください。");
    }
}

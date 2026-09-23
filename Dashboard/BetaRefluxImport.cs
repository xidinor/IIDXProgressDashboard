using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Import;

namespace IIDXProgressDashboard.Dashboard;

public sealed record BetaRefluxSession(Guid Id, string Path, RefluxImportOptions Options, string[]? AdditionalPaths = null)
{
    public bool MatchesPath(string path) => string.Equals(Path, path, StringComparison.OrdinalIgnoreCase)
        || AdditionalPaths?.Contains(path, StringComparer.OrdinalIgnoreCase) == true;
    public override string ToString() => $"{System.IO.Path.GetFileName(Path)} [{Id.ToString("N")[..8]}]";
}

/// <summary>Sessionの識別と設定をImporter呼出前に永続化する、Beta2の接続層。</summary>
public sealed class BetaRefluxImport(BetaSettings settings)
{
    private string Registry => System.IO.Path.Combine(settings.Directory, "reflux-sessions");

    public IReadOnlyList<BetaRefluxSession> ReadSessions()
    {
        if (!Directory.Exists(Registry)) return [];
        return Directory.GetFiles(Registry, "*.json").Order().Select(path =>
        {
            var session = JsonSerializer.Deserialize<BetaRefluxSession>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Session設定が空です。");
            if (session.Id == Guid.Empty || System.IO.Path.GetFileNameWithoutExtension(path) != session.Id.ToString("N")
                || string.IsNullOrWhiteSpace(session.Path) || session.Options is null)
                throw new InvalidDataException("Session設定が不正です。元の設定を復元してください。");
            session.Options.GetTimeZone();
            return session;
        }).ToArray();
    }

    public async Task<RefluxImportResult> ImportAsync(string path, Guid? existingId, RefluxImportOptions options)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(settings.DatabasePath)) throw new FileNotFoundException("Beta1の表示用DBを先に準備してください。");
        if (!File.Exists(path)) throw new FileNotFoundException("Session TSVが見つかりません。", path);
        options.GetTimeZone();
        Directory.CreateDirectory(Registry);
        // 別窓・別プロセスも含め、設定保存から取込完了まで同時実行を拒否する。
        using var gate = new FileStream(System.IO.Path.Combine(Registry, "import.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var sessions = ReadSessions();
        var samePath = sessions.FirstOrDefault(s => s.MatchesPath(path));
        var session = existingId is { } id
            ? sessions.Single(s => s.Id == id)
            : samePath;
        if (samePath is not null && session?.Id != samePath.Id)
            throw new InvalidOperationException("このパスは別のSessionとして登録済みです。対応するSessionを選択してください。");
        if (session is null)
        {
            session = new(Guid.NewGuid(), path, options);
            // 保存失敗ならDBには触れない。取込失敗後もIDを残して再利用する。
            using var file = new FileStream(System.IO.Path.Combine(Registry, session.Id.ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, session); file.Flush(true);
        }
        else if (session.Options != options)
            throw new InvalidOperationException("登録済みSessionの時刻設定は変更できません。");
        if (!session.MatchesPath(path))
        {
            // 明示されたコピー・改名先も記憶し、次回は同じIDへ戻す。
            session = session with { AdditionalPaths = [.. session.AdditionalPaths ?? [], path] };
            var destination = System.IO.Path.Combine(Registry, session.Id.ToString("N") + ".json");
            var temporary = destination + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, session); file.Flush(true); }
            File.Move(temporary, destination, overwrite: true);
        }
        return await new RefluxSessionTsvImporter(new DatabaseInitializer(settings.DatabasePath), settings.Directory)
            .ImportAsync(path, session.Id, session.Options);
    }
}

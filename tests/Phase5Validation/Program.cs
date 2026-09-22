using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Difficulty;
using IIDXProgressDashboard.Master;

// 任意の実入力検証専用。CI/solutionに組み込まず、個人履歴と既存DBを入力にしない。
if (args.Length != 4)
    throw new ArgumentException("Usage: Phase5Validation <textage-directory> <normal-html> <hard-html> <new-output-directory>");
var textage = Path.GetFullPath(args[0]);
var html = args.Skip(1).Take(2).Select(Path.GetFullPath).ToArray();
var output = Path.GetFullPath(args[3]);
var inputs = TextageSourceReader.RequiredFileNames.Select(n => Path.Combine(textage, n)).Concat(html).ToArray();
// 再実行で検証済みDBを上書きしない。原本ディレクトリ内への出力も拒否する。
Require(!Directory.Exists(output) && !File.Exists(output), "出力先は未作成ディレクトリにしてください。");
foreach (var directory in inputs.Select(p => Path.GetDirectoryName(p)!).Distinct())
    Require(!output.Equals(directory, StringComparison.OrdinalIgnoreCase) &&
        !output.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "入力ディレクトリ内へは出力できません。");
var hashes = inputs.ToDictionary(p => p, Hash);
Directory.CreateDirectory(output);
var database = new DatabaseInitializer(Path.Combine(output, "validation.db"));
database.Initialize();
var master = await new MasterDataProvider().ReadLocalAsync(textage, new(TextageEncoding.Utf8, IncludeCsComparison: true));
var masterUpdates = new MasterUpdateService(database, Path.Combine(output, "backups"));
await masterUpdates.ApplyAsync(await masterUpdates.PrepareAsync(_ => Task.FromResult(master)));
var immutable = Dump("songs", "charts", "song_aliases", "play_history");
using var client = new HttpClient();
var provider = new DifficultyTableProvider(client);
var parsed = new List<DifficultyParseResult>
{
    await provider.ReadSavedHtmlAsync(DifficultyTableKind.Sp11Normal, html[0]),
    await provider.ReadSavedHtmlAsync(DifficultyTableKind.Sp11Hard, html[1])
};
// ☆12の2表は同じHTTP原本を共有し、失敗時に保存データへ切り替えない。
var normal = await provider.FetchAsync(DifficultyTableKind.Sp12Normal);
Require(normal.IsValid && normal.Source != null, "☆12 HTTP取得・解析失敗: " + string.Join(";", normal.Diagnostics));
await File.WriteAllTextAsync(Path.Combine(output, "songs.json"), normal.Source!.Content, new UTF8Encoding(false));
parsed.Add(normal);
parsed.Add(await provider.ParseAsync(DifficultyTableKind.Sp12Hard, normal.Source));
var service = new DifficultyUpdateService(database, Path.Combine(output, "backups"));
var reports = new List<object>();
foreach (var input in parsed)
{
    Require(input.IsValid && input.ReadComplete, "入力不正: " + input.Table.Code);
    var plan = await service.PrepareAsync(input);
    Require(plan.CanApply && !plan.RequiresMissingConfirmation, "初回反映不可: " + input.Table.Code);
    var result = await service.ApplyAsync(plan);
    Require(result.ApplyStatus == "APPLIED", "初回未反映");
    var before = Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries");
    var repeat = await service.ApplyAsync(await service.PrepareAsync(input));
    Require(repeat.Counts.Added == 0 && repeat.Counts.Updated == 0 && repeat.Counts.Unchanged == result.Counts.Added, "再適用件数不整合");
    Require(before == Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries"), "再適用で表変更");
    // 通信失敗を製品Providerから流し、既存の実表が空や部分入力で置換されないことを確認する。
    using var failedClient = new HttpClient(new ForbiddenHandler());
    var failedInput = await new DifficultyTableProvider(failedClient).FetchAsync(input.Table.Kind);
    var failed = await service.ApplyAsync(await service.PrepareAsync(failedInput));
    Require(failed.Status == "FAILED" && failed.Counts == new DifficultyChangeCounts(0, 0, 0), "失敗結果不整合");
    Require(before == Dump("difficulty_tables", "difficulty_ranks", "difficulty_table_entries"), "失敗で表変更");
    Require(immutable == Dump("songs", "charts", "song_aliases", "play_history"), "マスター/履歴変更");
    reports.Add(new
    {
        table = input.Table.Code, rows = input.Rows.Count,
        source = new { input.Source!.RequestedUrl, input.Source.InputKind, input.Source.Sha256, input.Source.ByteLength,
            input.Source.StartedAt, input.Source.CompletedAt, input.Source.ETag, input.Source.LastModified, input.Source.SourceRevision },
        reasons = plan.Rows.SelectMany(r => r.Diagnostics).GroupBy(d => d.Code).ToDictionary(g => g.Key, g => g.Count()),
        statuses = plan.Rows.GroupBy(r => r.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
        first = new { result.Status, result.Counts, result.Unresolved },
        repeat = new { repeat.Status, repeat.Counts, repeat.Unresolved },
        failure = failed.Status, preserved = true
    });
    Console.WriteLine($"{input.Table.Code}: {result.Status}, added={result.Counts.Added}, unresolved={result.Unresolved}; repeat/failure preservation OK");
}
Require((string)Scalar("PRAGMA quick_check;")! == "ok" && Scalar("PRAGMA foreign_key_check;") == null, "DB整合性違反");
Require((long)Scalar("SELECT count(*) FROM play_history;")! == 0, "架空履歴生成");
Require((long)Scalar("SELECT count(*) FROM import_runs WHERE status='RUNNING';")! == 0, "未終了run");
foreach (var pair in hashes) Require(Hash(pair.Key) == pair.Value, "原本変更");
var evidence = new
{
    validatedAt = DateTimeOffset.UtcNow,
    master = new { songs = master.Songs.Count, charts = master.Charts.Count, master.ParserVersion,
        sources = master.Sources.Files.ToDictionary(p => p.Key, p => p.Value.Sha256),
        limitation = "Local Textage snapshot; live HTTP/catalog coverage in Issue #13 remains unverified." },
    tables = reports, entries = Scalar("SELECT count(*) FROM difficulty_table_entries;"),
    quickCheck = "ok", foreignKeyViolations = 0, originalHashesUnchanged = true,
    limitation = "Wiki SAVED_HTML, not production browser acquisition. Failure response is synthetic HTTP 403. No personal history imported."
};
await File.WriteAllTextAsync(Path.Combine(output, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("All assertions passed; evidence.json written.");

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
object? Scalar(string sql)
{ using var connection = database.OpenConnection(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
string Dump(params string[] tables)
{
    using var connection = database.OpenConnection();
    var rows = new List<object?[]>();
    // テーブル名はこの検証コード内の固定リストだけ。外部入力をSQLへ渡さない。
    foreach (var table in tables)
    {
        using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
    }
    return JsonSerializer.Serialize(rows);
}
sealed class ForbiddenHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Synthetic challenge") });
}

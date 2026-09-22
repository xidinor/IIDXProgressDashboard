using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Difficulty;
using Microsoft.Web.WebView2.Core;

// ネットワーク・Runtimeを使う任意検証。CIには含めず、元DBを一切開かない。
if (args.Length != 1) throw new ArgumentException("存在しない出力ディレクトリを指定してください。");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("出力先は既に存在します。");
Directory.CreateDirectory(output);
using var client = new HttpClient();
var browser = new WebView2DifficultyFetcher(Path.Combine(output, "profile"));
var provider = new DifficultyTableProvider(client, browser.FetchAsync);
var evidence = new List<object>();
bool valid = true;
foreach (var kind in new[] { DifficultyTableKind.Sp11Normal, DifficultyTableKind.Sp11Hard })
{
    var result = await provider.FetchAsync(kind);
    valid &= result.IsValid;
    if (result.Source is { } source)
        await File.WriteAllTextAsync(Path.Combine(output, $"{kind}.html"), source.Content, new UTF8Encoding(false));
    var entry = new
    {
        table = result.Table.Code, result.AcquisitionStatus, result.ParseStatus, result.ReadComplete,
        rows = result.Rows.Count, result.Diagnostics,
        source = result.Source is { } s ? new { s.RequestedUrl, s.FinalUrl, s.InputKind, s.Sha256, s.ByteLength, s.StartedAt, s.CompletedAt, s.HttpStatus } : null
    };
    evidence.Add(entry);
    Console.WriteLine(JsonSerializer.Serialize(entry));
}
await File.WriteAllTextAsync(Path.Combine(output, "evidence.json"), JsonSerializer.Serialize(new
{
    runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(),
    tables = evidence
}, new JsonSerializerOptions { WriteIndented = true }));
return valid ? 0 : 1;

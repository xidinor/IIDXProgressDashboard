using System.Net.Http;
using System.Text.Json;
using IIDXProgressDashboard.Master;

// 任意の実HTTP検証。CIから実行せず、個人DB・履歴の読書きを行わない。
if (args is not ["--fetch"])
{
    Console.Error.WriteLine("公開配信の取得・解析のみを検証する場合は --fetch を指定してください。");
    return 1;
}
using var client = new HttpClient();
var snapshot = await new IidxDataTableProvider().FetchAsync(client);
Console.WriteLine(JsonSerializer.Serialize(new
{
    snapshot.AcquiredAt, snapshot.Fingerprint, songs = snapshot.Songs.Count,
    minimumId = snapshot.Songs.Min(s => s.ExternalSongId), maximumId = snapshot.Songs.Max(s => s.ExternalSongId),
    multipleIdsPerTag = snapshot.Songs.GroupBy(s => s.Tag).Count(g => g.Count() > 1),
    ambiguousTitles = snapshot.Songs.GroupBy(s => s.NormalizedTitle).Count(g => g.Select(s => s.Tag).Distinct().Count() > 1),
    sources = snapshot.Sources.Select(s => new { s.Name, s.Url, s.Sha256 })
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;

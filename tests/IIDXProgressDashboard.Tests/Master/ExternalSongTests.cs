using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Master;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IIDXProgressDashboard.Tests.Master;

public sealed class ExternalSongTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IIDXProgressDashboard.Tests", Guid.NewGuid().ToString("N"));
    private DatabaseInitializer Database => new(Path.Combine(directory, "output.db"));
    private ExternalSongUpdateService Service => new(Database, Path.Combine(directory, "backups"));
    private const string Info = "{\"1\":{\"artist\":\"Artist\",\"genre\":\"Genre\",\"version\":1}}";
    public ExternalSongTests()
    {
        Database.Initialize();
        Sql("INSERT INTO songs(tag,title,normalized_title) VALUES('one','Internal','INTERNAL'),('two','Other','OTHER');");
    }

    internal static ExternalSongSnapshot Snapshot(string title = "Ｅｘｔｅｒｎａｌ", string tag = "one", DateTimeOffset? at = null) =>
        new IidxDataTableProvider().Parse(Encoding.UTF8.GetBytes(Info),
            JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["1"] = title }),
            JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["1"] = tag }), at ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task RegistrationPreservesInternalTitleAndUpdatesExternalTitleIdempotently()
    {
        var first = Snapshot();
        var result = await Service.UpdateAsync(_ => Task.FromResult(first));
        Assert.Equal(("SUCCESS", 1, 1, 0), (result.Status, result.Read, result.Imported, result.Unresolved));
        Assert.Equal("EXTERNAL", Sql("SELECT normalized_title FROM external_song_ids;"));
        var created = Sql("SELECT created_at FROM external_song_ids;");
        await Service.UpdateAsync(_ => Task.FromResult(first));
        await Service.UpdateAsync(_ => Task.FromResult(Snapshot("Renamed")));
        Assert.Equal(1L, Sql("SELECT count(*) FROM external_song_ids;"));
        Assert.Equal(created, Sql("SELECT created_at FROM external_song_ids;"));
        Assert.Equal("Renamed", Sql("SELECT title FROM external_song_ids;"));
        Assert.Equal("Internal", Sql("SELECT title FROM songs WHERE tag='one';"));
        Assert.True(File.Exists(result.BackupPath));
        Assert.Contains("song-info.json", (string)Sql("SELECT options_json FROM import_runs LIMIT 1;")!);
    }

    [Fact]
    public async Task MissingAnchorIsPendingAndCanBeRegisteredAfterMasterArrives()
    {
        var snapshot = Snapshot(tag: "later");
        var first = await Service.UpdateAsync(_ => Task.FromResult(snapshot));
        Assert.Equal(("PARTIAL", 0, 1), (first.Status, first.Imported, first.Unresolved));
        Assert.Equal("EXTERNAL_TAG_NOT_FOUND", Sql("SELECT reason_code FROM unresolved_imports;"));
        Assert.Contains("later", (string)Sql("SELECT raw_data FROM unresolved_imports;")!);
        Sql("INSERT INTO songs(tag,title,normalized_title,is_active) VALUES('later','Later','LATER',0);");
        Assert.Equal(1, (await Service.UpdateAsync(_ => Task.FromResult(snapshot))).Imported);
        Assert.Equal("RESOLVED", Sql("SELECT status FROM unresolved_imports;"));
        Assert.Equal(0L, Sql("SELECT is_active FROM songs WHERE tag='later';"));
    }

    [Theory]
    [InlineData("reassigned")]
    [InlineData("missing")]
    [InlineData("stale")]
    public async Task UnsafeUpdateIsHeldWithExistingMappingIntact(string kind)
    {
        var first = Snapshot();
        await Service.UpdateAsync(_ => Task.FromResult(first));
        var next = kind == "reassigned" ? Snapshot(tag: "two") :
            kind == "stale" ? Snapshot(at: first.AcquiredAt.AddDays(-1)) :
            new IidxDataTableProvider().Parse(Encoding.UTF8.GetBytes(Info.Replace("\"1\":", "\"2\":")),
                "{\"2\":\"External\"}"u8.ToArray(), "{\"2\":\"two\"}"u8.ToArray(), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.UpdateAsync(_ => Task.FromResult(next)));
        Assert.Equal("one", Sql("SELECT tag FROM external_song_ids;"));
        Assert.Equal(1L, Sql("SELECT count(*) FROM external_song_ids;"));
        Assert.Equal("FAILED", Sql("SELECT status FROM import_runs ORDER BY import_run_id DESC LIMIT 1;"));
    }

    [Fact]
    public async Task OutOfOrderCompletionDoesNotPermitOlderSnapshotToOverwriteLatest()
    {
        // run IDは取得開始順であり、反映時点の新旧比較には使えない。
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ExternalSongSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = Service.UpdateAsync(_ => { started.SetResult(); return release.Task; });
        await started.Task;
        var time = DateTimeOffset.UtcNow;
        await Service.UpdateAsync(_ => Task.FromResult(Snapshot("Old", at: time)));
        release.SetResult(Snapshot("Latest", at: time.AddMinutes(2)));
        await late;
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.UpdateAsync(_ => Task.FromResult(Snapshot("Middle", at: time.AddMinutes(1)))));
        Assert.Equal("Latest", Sql("SELECT title FROM external_song_ids;"));
    }

    [Fact]
    public async Task AcquisitionFailureCancellationAndSqlFailureLeaveNoMappings()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => Service.UpdateAsync(_ => throw new HttpRequestException("offline")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.UpdateAsync(_ => Task.FromResult(Snapshot()), new CancellationToken(true)));
        // INSERT成功後の完了ログで故意に失敗させ、対応表も元へ戻ることを確認する。
        await Assert.ThrowsAsync<SqliteException>(() => Service.UpdateAsync(_ =>
        {
            Sql("CREATE TRIGGER fail_complete BEFORE UPDATE ON import_runs WHEN NEW.status='SUCCESS' BEGIN SELECT RAISE(ABORT,'test'); END;");
            return Task.FromResult(Snapshot());
        }));
        Assert.Equal(0L, Sql("SELECT count(*) FROM external_song_ids;"));
        Assert.Equal(3L, Sql("SELECT count(*) FROM import_runs WHERE status='FAILED';"));
    }

    [Theory]
    [InlineData("{}", "{}", "{}")]
    [InlineData(Info, "{\"2\":\"Song\"}", "{\"1\":\"one\"}")]
    [InlineData(Info, "{\"1\":\"Song\",\"1\":\"Other\"}", "{\"1\":\"one\"}")]
    [InlineData(Info, "{\"01\":\"Song\"}", "{\"1\":\"one\"}")]
    [InlineData(Info, "{\"-1\":\"Song\"}", "{\"1\":\"one\"}")]
    [InlineData(Info, "{\"1\":\"　\"}", "{\"1\":\"one\"}")]
    [InlineData(Info, "{\"1\":\"Song\"}", "{\"1\":\" one\"}")]
    [InlineData("{\"1\":{}}", "{\"1\":\"Song\"}", "{\"1\":\"one\"}")]
    public void InvalidOrPartialJsonNeverBecomesSnapshot(string info, string title, string tag) =>
        Assert.Throws<InvalidDataException>(() => new IidxDataTableProvider().Parse(Encoding.UTF8.GetBytes(info),
            Encoding.UTF8.GetBytes(title), Encoding.UTF8.GetBytes(tag), DateTimeOffset.UtcNow));

    [Fact]
    public async Task FetchChecksAllFilesTwiceAndCachesOnlyCompleteSnapshot()
    {
        var handler = new Handler((_, _) => HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var provider = new IidxDataTableProvider();
        var first = await provider.FetchAsync(client);
        Assert.Same(first, await provider.FetchAsync(client));
        Assert.Equal(6, handler.Count);
        Assert.Equal(3, first.Sources.Count);
    }

    [Fact]
    public async Task ChangingInputAndHttpFailureAreNotEmptySuccess()
    {
        using var changing = new HttpClient(new Handler((_, _) => HttpStatusCode.OK, change: true));
        await Assert.ThrowsAsync<InvalidDataException>(() => new IidxDataTableProvider().FetchAsync(changing));
        var denied = new Handler((_, _) => HttpStatusCode.Forbidden);
        using var failed = new HttpClient(denied);
        await Assert.ThrowsAsync<HttpRequestException>(() => new IidxDataTableProvider().FetchAsync(failed));
        Assert.Equal(1, denied.Count);
        var transient = new Handler((n, _) => n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        using var retry = new HttpClient(transient);
        Assert.Single((await new IidxDataTableProvider().FetchAsync(retry)).Songs);
        Assert.Equal(7, transient.Count);
    }

    private sealed class Handler(Func<int, string, HttpStatusCode> status, bool change = false) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            var text = name == "song-info.json" ? Info : name == "title.json" ? "{\"1\":\"External\"}" : "{\"1\":\"one\"}";
            if (change && Count > 3) text += " ";
            return Task.FromResult(new HttpResponseMessage(status(Count, name)) { Content = new StringContent(text) });
        }
    }

    private object? Sql(string text)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand(); command.CommandText = text; return command.ExecuteScalar();
    }
    public void Dispose() => Directory.Delete(directory, true);
}

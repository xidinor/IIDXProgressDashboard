using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace IIDXProgressDashboard.Difficulty;

/// <summary>☆11の固定URLからDOMを採取する。解析・キャッシュ・DB更新は既存の各サービスへ委ねる。</summary>
public sealed class WebView2DifficultyFetcher(string userDataFolder)
{
    private readonly string profilePath = Path.GetFullPath(userDataFolder);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<DifficultySource> FetchAsync(DifficultyTableKind kind, CancellationToken token = default)
    {
        var table = DifficultyTableDefinition.Get(kind);
        if (table.Level != 11) throw new ArgumentException("WebView2取得は☆11 Wiki専用です。", nameof(kind));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // WebView2はSTAとmessage pumpが必須。呼出側のUIを止めず、専用STA内だけで操作・破棄する。
            var completion = new TaskCompletionSource<DifficultySource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => Run(table, token, completion)) { IsBackground = true, Name = "Difficulty Wiki WebView2" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private void Run(DifficultyTableDefinition table, CancellationToken token, TaskCompletionSource<DifficultySource> completion)
    {
        DifficultySource? source = null;
        Exception? failure = null;
        try
        {
            using var host = new Form { ShowInTaskbar = false, Opacity = 0, Width = 1024, Height = 768 };
            using var view = new WebView2 { Dock = DockStyle.Fill };
            host.Controls.Add(view);
            host.Shown += async (_, _) =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                var started = DateTimeOffset.UtcNow;
                string stage = "Runtime初期化";
                try
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath).WaitAsync(timeout.Token);
                    await view.EnsureCoreWebView2Async(environment).WaitAsync(timeout.Token);
                    var core = view.CoreWebView2;
                    core.Settings.AreDefaultScriptDialogsEnabled = false;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.AreHostObjectsAllowed = false;
                    core.Settings.IsWebMessageEnabled = false;
                    core.NewWindowRequested += (_, e) => e.Handled = true;
                    core.DownloadStarting += (_, e) => e.Cancel = true;
                    core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                    var navigation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int? documentStatus = null;
                    core.WebResourceResponseReceived += (_, e) =>
                    {
                        if (e.Request.Uri == table.Url && e.Request.Method == "GET") documentStatus = e.Response.StatusCode;
                    };
                    // 広告等の副資源を待つNavigationCompletedだけには依存しない。
                    // HTMLのDOM構築完了後、表全体の完全性は既存Parserで検査する。
                    core.DOMContentLoaded += (_, _) => navigation.TrySetResult(documentStatus ?? 0);
                    core.NavigationStarting += (_, e) =>
                    {
                        if (e.Uri != table.Url)
                        {
                            e.Cancel = true;
                            navigation.TrySetException(new InvalidDataException("対象外URLへの遷移を拒否しました。"));
                        }
                    };
                    core.NavigationCompleted += (_, e) =>
                    {
                        // 403等でも本文があればDOMを保全し、既存Parserに失敗として判定させる。
                        if (!e.IsSuccess && e.HttpStatusCode == 0)
                            navigation.TrySetException(new IOException($"WebView2 navigation: {e.WebErrorStatus}"));
                        else if (!e.IsSuccess) navigation.TrySetResult(e.HttpStatusCode);
                    };
                    stage = "ページDOM構築";
                    core.Navigate(table.Url);
                    int status = await navigation.Task.WaitAsync(timeout.Token);
                    if (status == 0) throw new IOException("ページのHTTP状態を取得できませんでした。");
                    stage = "DOM採取";
                    // 固定式のみ実行する。取得本文をevalせず、DOM全体をそのまま既存Parserへ渡す。
                    string json = await core.ExecuteScriptAsync("(() => { const html = document.documentElement.outerHTML; return new TextEncoder().encode(html).length <= 8388608 ? html : null; })()").WaitAsync(timeout.Token);
                    string html = JsonSerializer.Deserialize<string>(json) ?? throw new InvalidDataException("DOMが取得できませんでした。");
                    if (Encoding.UTF8.GetByteCount(html) > DifficultySource.MaxBytes)
                        throw new InvalidDataException("入力上限8 MiBを超えています。");
                    source = DifficultySource.FromUtf8(table.Url, Encoding.UTF8.GetBytes(html), "BROWSER_DOM") with
                    {
                        FinalUrl = core.Source, StartedAt = started, CompletedAt = DateTimeOffset.UtcNow,
                        HttpStatus = status
                    };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { failure = new OperationCanceledException(token); }
                catch (OperationCanceledException) { failure = new IOException($"WebView2取得が45秒でタイムアウトしました ({stage})。"); }
                catch (Exception ex)
                {
                    // Runtime・profileエラーのOS本文には個人パスが含まれ得るため診断へ転記しない。
                    failure = new IOException($"WebView2取得に失敗しました ({ex.GetType().Name})。");
                }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        catch (Exception ex) { failure = new IOException($"WebView2を起動できませんでした ({ex.GetType().Name})。"); }

        // Controlを破棄してから次の取得を許可する。同一profileの同時使用を避ける。
        if (failure is OperationCanceledException) completion.TrySetCanceled(token);
        else if (failure != null) completion.TrySetException(failure);
        else if (source != null) completion.TrySetResult(source);
        else completion.TrySetException(new IOException("WebView2が取得完了前に終了しました。"));
    }
}

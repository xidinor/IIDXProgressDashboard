# IIDXProgressDashboard

IIDX / INFINITASの実プレイ履歴から、クリア状況とスコア・BPの推移を表示するWindowsアプリケーションです。

現在は **Phase 6の画面確認用ベータ版** を実装中です。Form1で起動し、新形式DBの4表のランク別集計、直近結果の譜面一覧、検索、譜面別の履歴・グラフを表示します。未プレイとNO PLAY履歴あり、BP欠損と0を区別します。

## ベータ版の起動

ローカルの準備済みアプリは `artifacts/beta-preview/IIDXProgressDashboard.exe`。本体横のINIと表示用DBを使用します。旧形式DBを通常DBとして直接開くことはできません。

**必要ファイル、旧履歴の事前準備、表示仕様は [Beta1の説明](docs/phase6-beta-display.md) を参照してください。** Beta2では「Reflux取込」からSession TSVを同じDBへ追加でき、一覧・グラフを再読込します。Sessionの再利用・時刻設定・検証範囲は [Beta2の説明](docs/phase6-beta2-reflux.md) を参照してください。Beta3では起動時に未解決行があれば確認を促し、旧履歴・Refluxの元行を画面で手動確定できます。同条件の元行は対象件数を確認して一括確定できます。操作と対象範囲は [Beta3の説明](docs/phase6-manual-play-resolution.md) を参照してください。起動時の自動取込・Web取得は行いません。

## 開発

Windows / .NET 8 SDK / Windows Formsを使用します。

```powershell
dotnet build IIDXProgressDashboard.sln
dotnet test tests/IIDXProgressDashboard.Tests/IIDXProgressDashboard.Tests.csproj --filter FullyQualifiedName~BetaDisplayTests
```

既存のNuGet互換性警告と旧コードの警告が残っています。self-contained / single-file配布、GAC参照の除去、依存Runtimeの配布確認は後続フェーズです。

## データと実装

- 曲はsongs.tag、譜面はcharts.chart_idで識別します。実プレイを保存し、最高値・直近値・進捗は履歴から計算します。
- 入力は楽曲・譜面マスター、非公式難易度表、旧形式SQLite履歴、Reflux Session TSVです。Pickleの直接取込、best一覧からの履歴生成は行いません。
- `data/` の原本は保全し、表示用DBへ別途取り込みます。個人DB・入力・INIはGitへ登録しません。
- Pythonは移植元資料です。新しい通常画面は既存C#の取込・DB基盤と接続し、Pythonを実行しません。

|Phase|解説|
|---|---|
|1 新DB基盤|[初期化・Migration](Database/README.md)|
|2 マスター・照合|[マスター・譜面照合](docs/phase2-master-matching-explained.md)|
|3 旧履歴|[Legacy Importer](docs/phase3-legacy-importer-explained.md)|
|4 Reflux|[Session Importer](docs/phase4-reflux-session-importer-explained.md)|
|5 非公式難易度表|[4表の管理](docs/phase5-difficulty-tables-explained.md)|
|6 UI接続|[Beta1](docs/phase6-beta-display.md)・[Beta2](docs/phase6-beta2-reflux.md)・[Beta3](docs/phase6-manual-play-resolution.md)|
|7 配布・旧依存除去|後続|

作業ルールは [AGENTS.md](AGENTS.md)、ライセンスは [MIT License](LICENSE.txt) を参照してください。

# IIDXProgressDashboard

IIDX / INFINITAS のプレイ履歴をもとに、クリア状況やスコア・BP（ミスカウント）の推移、非公式難易度表ごとの進捗を可視化する Windows 向けデスクトップアプリケーションです。

別添ドキュメントに沿って段階的に改修しています。新DB基盤（Phase 1）の初期DDL・初期化・Migration管理を実装しました。利用方法と保全方針は [Database/README.md](Database/README.md) を参照してください。マスター取得・履歴取込・既存UIの新DB接続は後続フェーズです。

## 現在できることと制限

- 起動画面でレベル11／12を選び、「読み込み」から非公式難易度ランク別のランプ件数を表示します。表示できる範囲は用意したマスターデータに依存します。
- 現在の読み込みは初期開発時の未公開データを使用しています。ユーザーが持つ過去の履歴としては、打鍵カウンタv2の `alllog.pkl` を想定しています。
- スコア・BPグラフの `GraphForm` と履歴取得処理はありますが、曲検索・グラフ表示への導線は現在の起動画面に接続されていません。

現行のランプ集計は曲名単位で、SP/DPやHYPER/ANOTHERなどの別譜面を区別できません。また、一部の旧ランプ表記を正しく変換できないため、表示結果には改善が必要です。今後、譜面単位の照合・集計へ置き換えます。

## 開発環境

- Windows
- .NET 8 SDK（対象フレームワーク: `net8.0-windows`）
- Windows Forms
- Microsoft.Data.Sqlite
- ScottPlot.WinForms

Visual Studioを使用する場合は、.NETデスクトップ開発のワークロードを用意して `IIDXProgressDashboard.sln` を開きます。

現行プロジェクトには `System.Windows.Forms.DataVisualization` への環境依存のGAC参照があります。参照を解決できない場合は、プロジェクトファイルの `HintPath` と環境を確認してください。配布時にこの依存をどう扱うかは今後の確認対象です。

## ビルドと起動

リポジトリ直下で実行します。

```powershell
dotnet restore IIDXProgressDashboard.sln
dotnet build IIDXProgressDashboard.sln
```

打鍵カウンタv2の `alllog.pkl` を置く場合は、以下のように `data/` に配置します。個人データはリポジトリに同梱していません。

```text
IIDXProgressDashboard/
├── IIDXProgressDashboard.csproj
└── data/
    └── alllog.pkl
```

`alllog.pkl` のアプリからの直接取込は今後対応予定です。現時点では、配置だけで履歴を表示できるわけではありません。

```powershell
dotnet run --project IIDXProgressDashboard.csproj
```

初期開発用データがある環境では、起動後にレベルを選び、「読み込み」を押します。

DB接続パスは現在、作業ディレクトリに対する相対パスです。`data/` の自動探索やDB選択UIはありません。IDEや実行ファイルから起動する場合も、作業ディレクトリに注意してください。DBがない場合やスキーマが異なる場合は、読み込みに失敗します。

上記は現行コードに基づく開発手順です。環境ごとのビルド・起動確認は別途必要です。

## データの扱い

`data/` はローカルの移行元・調査用データの置き場です。原本には初期化・Migration・取込結果を書き込まず、新DBは別の出力先に作成する方針です。

`python/` のスクリプトは既存データ処理の移植元です。新仕様ではデータ処理をC#へ統一し、Python Runtimeへの依存をなくします。

## 改善・統合の方針

基本方針は、**実際のプレイを保存し、ベストや進捗はその履歴から算出する**ことです。

- `play_history` の1行を実際の1プレイとし、スコアやBPが悪化した履歴も保持します。
- 曲は `songs.tag`、譜面は `charts.chart_id` で識別し、SP/DP・B/N/H/A/Lを区別します。
- 通常利用のDBを `iidx-progress.db` に統合し、スキーマのバージョンを管理します。
- 安全に照合できないデータは推測で登録せず、未解決データとして保存します。
- ☆11・☆12のNORMAL/HARD難易度表を、それぞれ独立して扱います。
- 最終的に .NET 8 のself-contained / single-fileでの配布を予定しています。

### v1で予定する入力

| 入力 | 用途 |
| --- | --- |
| 楽曲・譜面マスター | C#のProviderによる取得・更新 |
| 非公式難易度表 | 表ごとのランク・譜面対応の取得・更新 |
| 打鍵カウンタv2の `alllog.pkl` | 過去の実プレイ履歴の取込 |
| Reflux Session TSV | 新しい実プレイ履歴の取込 |

これらは新設計の対応予定です。現時点で新Importerから取り込めることを示すものではありません。RefluxはSession形式を対象とし、全曲best/tracker TSVや打鍵カウンタbest CSV、KONAMI公式CSVはv1の対象外です。現在ベストだけの一覧からプレイ履歴を生成しません。

### 実装順序

| Phase | 内容 |
| --- | --- |
| 1 | 新DB基盤・初期DDL・Migration |
| 2 | マスター取得・曲名正規化・譜面照合 |
| 3 | 旧プレイ履歴の移行 |
| 4 | Reflux Session TSV取込 |
| 5 | 非公式難易度表の取得・管理 |
| 6 | 既存UIの新DB接続、曲別スコア・BP履歴表示 |
| 7 | 旧DBの通常利用依存・Pythonスクリプトの除去 |

## 開発時のルール

作業方針、実データの調査結果、検証項目、未確定事項は [AGENTS.md](AGENTS.md) を参照してください。データに関する記載は調査時点の情報であり、ファイル追加・更新時に再確認します。

コード変更時は `dotnet build IIDXProgressDashboard.sln` を実行し、変更した機能に応じた検証を行います。DB基盤の自動テストは `dotnet test tests/IIDXProgressDashboard.Tests/IIDXProgressDashboard.Tests.csproj` で実行します。個人データは使用しません。

## ライセンス

[MIT License](LICENSE.txt)

# Phase 2-1：取得元・データ仕様・更新範囲の調査と確定

後続実装：[Phase 2-2の取得・解析](phase2-2-master-provider-explained.md)。以下の先行Readerの説明は実装時点の記録であり、現在のoptions・4ファイル入力・CP932対応は後続文書を参照。

対象は2026-09-07時点。[Phase 2 Issue #4](https://github.com/xidinor/IIDXProgressDashboard/issues/4)の項目1を指す。確定した入力・更新契約は[調査仕様](phase2-1-textage-source-contract.md)を参照。Phase 2全体の完了ではない。以下の読込基盤は項目2に属する先行実装として区別する。

## 実装した処理

```mermaid
flowchart LR
    Files[ローカルのJS 7ファイル] --> Reader[TextageSourceReader]
    Reader --> Check[不足・空入力・UTF-8検証]
    Check --> Snapshot[内容・バイト数・SHA-256]
    Snapshot -. 後続工程 .-> Parser[構文解析・モデル変換]
    Parser -. 後続工程 .-> DB[検証・UPSERT]
```

[TextageSourceReader.cs](../Master/Textage/TextageSourceReader.cs)は指定ディレクトリから非同期で読み込む。必須ファイルの不足は一覧で報告し、空入力や不正UTF-8は例外にする。BOMは内容から取り除くが、ハッシュはBOMを含む元バイト列から計算する。キャンセルにも対応する。

```csharp
var snapshot = await new TextageSourceReader().ReadAsync(inputDirectory, cancellationToken);
var titles = snapshot.Files["titletbl.js"].Content;
```

JSの評価・実行、ネットワーク取得、DB接続は行わない。全ファイルを読み込めた場合だけ結果を返す。途中失敗で部分結果を返したり、入力原本を書き換えたりしない。DB更新がないためトランザクションやimport_runs記録はまだない。

このsnapshotは読み取った内容のまとまりであり、複数ファイルの同時点取得を保証するものではない。読み込み中の入力更新は避ける。読込成功だけでマスターの完全性や非アクティブ化の許可を判断してはならない。

## 配置データの確認結果

旧[buildsongmaster.py](../python/buildsongmaster.py)が参照する7ファイルはすべて存在し、空ではなく、厳密なUTF-8でデコードできた。

| ファイル | 用途 |
| --- | --- |
| titletbl.js | tag、曲名、ジャンル、アーティスト、バージョン番号など |
| scrlist.js | vertbl、バージョン追加代入、収録・表示条件の調査資料 |
| datatbl.js | 譜面別ノーツ数 |
| actbl.js | レベルとフラグ |
| cstbl.js / cstbl1.js / cstbl2.js | CS版別テーブル |

追加のcltbl.jsとstepup.jsも配置済み。今回の読込対象には含めない。data配下のJSは[.gitignore](../.gitignore)で除外した。

**現在の読込基盤に必要なファイル不足はない。** 追加調査で配信元URLを確認し、7ファイルとも配信バイトをCP932で復号した文字列がローカルUTF-8と一致した。元の保存日時・取得履歴は不明であり、上流の同時点スナップショット保証はない。全タグの相互整合性・全譜面の妥当性検証は後続のParserで行う。

## 後続の解析で反映する点

- titletblにはブロックコメント、SS定数、fontcolor呼出し、サブタイトル中のHTML、ダミーtagがある。旧Pythonのeval失敗を無視する方式では行が欠落する。JSを実行せず、対応するデータ構文を限定して解析し、未対応構文は明示的に報告する。
- scrlistのvertblには配列定義の後に35番へのsubstream追加代入がある。配列リテラルだけの抽出では不足する。
- cstblは版ごとに分かれている。配置ファイルではcstbl[1]〜[17]を確認した。tagだけで全CS版を上書き結合すると版の違いを失うため、旧Pythonの結合順を移植しない。
- actblはレベルとフラグの対を持つ。scrlistにはINFINITASの収録条件やBEGINNER・LEGGENDARIAの分岐があり、表示経路間でも条件に差がある。対象範囲・フラグの意味を整理してから変換する。
- 当面、旧Pythonの配列マッピングは調査資料として扱う。SP/DP・B/N/H/A/L、旧BEGINNER列、ノーツ0・欠落、CSとの衝突を合成fixtureで検証してから採用する。

## 検証と残課題

[TextageSourceReaderTests.cs](../tests/IIDXProgressDashboard.Tests/Master/Textage/TextageSourceReaderTests.cs)は合成入力で、JSを実行しないこと、BOM・ハッシュ・原本保持、複数ファイル不足、空入力、不正UTF-8、キャンセルを検証する。個人のdataには依存しない。実データのファイル存在・UTF-8確認は別途PowerShellで行った。

ビルドは成功（既存パッケージ互換性・Nullable等の警告あり）。自動テストは既存18件と追加5件の計23件が成功した。DBスキーマ・既存DB・既存UIへの変更はない。

後続工程は安全な構文解析、曲・譜面モデルへの変換、対象範囲と完全性判定、TitleNormalizer、chart_idを維持する更新、ChartResolver。今回の読込結果をそのままDBへ登録することはできない。

## 2026-09-11：Issue #4 項目6.1に合わせたテスト更新

Readerの公開入力・戻り値・例外・ファイルへの副作用を検証するブラックボックステストに整理した。基本fixtureは独立して定義した7ファイルの合成Textage形式とし、実装のRequiredFileNamesから生成しない。Parserは未実装なので、このfixtureのParser成功までは検証していない。

| Issueの入力条件・保証 | 対応テスト |
| --- | --- |
| 正しいファイル名とUTF-8、BOM有無、元バイト長・SHA-256・原本保持 | ReadsUtf8AndPreservesOriginalBytes |
| 空ファイル・空白・BOMのみ | RejectsEmptyOrWhitespaceInput |
| 不正UTF-8、切断、バイナリ、CP932・UTF-16へのfallback禁止 | RejectsInvalidUtf8WithoutFallbackOrReplacement |
| でたらめなUTF-8、実行可能に見える文字列を実行せず返す | ReturnsUtf8TextWithoutParsingOrExecuting |
| 誤ったファイル名（内容が正しい／不正） | RejectsWrongFileNameRegardlessOfContent |
| 複数の必須ファイル不足 | ReportsAllMissingFiles |
| 必須ファイル間での内容入替えはReaderでは成功 | ReturnsSwappedContentsWithoutSemanticValidation |
| 正常入力と余分な不正バイナリファイル | IgnoresUnrelatedFileEvenWhenItsEncodingIsInvalid |
| 事前キャンセルを無視しない | SupportsCancellation |

不正バイトは再現可能な固定列を使う。最後の必須ファイルが不正でも部分snapshotを返さず、原本を補正しないことを確認する。正常入力では改行・全角空白・結合文字も保持する。

2026-09-11実行結果：ソリューションのビルド成功（既存警告27件、エラー0）、テスト37件成功（既存DB18件、Reader19件）、スキップ0。Reader本体・DB・実データへの変更はない。

Issueの項目6.2〜6.6（Parser内部状態、Provider更新の保全、正規化・alias・Resolver、統合）と6.7のImporter予約は、各実装時に追加する。今回はそれらを空のテストや成功する代用品で置き換えていない。読込途中のキャンセルを決定的に発生させるテストは未追加であり、今回のキャンセル検証は事前キャンセルに限定する。

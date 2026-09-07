# Phase 2-1：取得元・データ仕様・更新範囲の調査と確定

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

[TextageSourceReader.cs](../Master/TextageSourceReader.cs)は指定ディレクトリから非同期で読み込む。必須ファイルの不足は一覧で報告し、空入力や不正UTF-8は例外にする。BOMは内容から取り除くが、ハッシュはBOMを含む元バイト列から計算する。キャンセルにも対応する。

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

[TextageSourceReaderTests.cs](../tests/IIDXProgressDashboard.Tests/TextageSourceReaderTests.cs)は合成入力で、JSを実行しないこと、BOM・ハッシュ・原本保持、複数ファイル不足、空入力、不正UTF-8、キャンセルを検証する。個人のdataには依存しない。実データのファイル存在・UTF-8確認は別途PowerShellで行った。

ビルドは成功（既存パッケージ互換性・Nullable等の警告あり）。自動テストは既存18件と追加5件の計23件が成功した。DBスキーマ・既存DB・既存UIへの変更はない。

後続工程は安全な構文解析、曲・譜面モデルへの変換、対象範囲と完全性判定、TitleNormalizer、chart_idを維持する更新、ChartResolver。今回の読込結果をそのままDBへ登録することはできない。

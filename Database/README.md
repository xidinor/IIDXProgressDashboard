# Phase 1: 新DB基盤

`Migrations/001_initial.sql` は `SPECS.md` 第6章の10テーブルとインデックスを定義する。SQLは埋め込みリソースとして配布し、作業ディレクトリや外部SQLファイルに依存しない。

仕様のDDLからトランザクション開始・終了、外部キーの有効化、Migration履歴のINSERTをC#側へ移した。テーブル・制約の定義は変更していない。

## 利用方法

```csharp
using IIDXProgressDashboard.Database;

// 入力原本とは別の出力先を明示する。
var database = new DatabaseInitializer(Path.Combine("output", "iidx-progress.db"));
database.Initialize();
using var connection = database.OpenConnection();
```

`Initialize()` は出力ディレクトリを作成し、空のDBにv1を適用する。`OpenConnection()` は既存ファイルだけを開き、接続ごとに外部キーを有効にする。返した接続は呼び出し側がDisposeする。

現在のForm1への接続はPhase 6で行う。この段階ではアプリ起動時に新DBを作成しない。通常利用の保存場所も後続のUI設計で決定する。

## Migrationと既存DBの保全

- 即時トランザクション内でスキーマ検査・DDL・バージョン記録を行う。失敗時はDDLと履歴をまとめてロールバックする。作成済みの空ファイルやディレクトリは残る場合がある。
- v1再実行時は、埋め込みDDLから作った基準スキーマと全テーブル・インデックス等のSQL定義を照合し、Migration履歴がversion 1の1行であることを確認する。
- 旧DB、履歴欠落、将来バージョン、改変されたスキーマは変更せずエラーにする。SQL定義の厳密比較のため、外部ツールで同等のDDLへ書き換えたDBも拒否する。
- Phase 1では非空DBのアップグレードは行わない。将来v2以降を追加する際は、適用前のSQLiteバックアップと、接続を閉じてバックアップから復旧する手順を先に実装・検証する。
- `data/` は入力原本用。出力先に指定しない。Importer実装時には入力・出力の絶対パスの相違も検証する。

## 検証

```powershell
dotnet build IIDXProgressDashboard.sln
dotnet test tests/IIDXProgressDashboard.Tests/IIDXProgressDashboard.Tests.csproj
```

テストは一時ディレクトリの合成DBのみを使用する。新規作成・再実行・履歴保持・外部キー・一意制約・CHECK制約・別譜面とNORMAL/HARDの独立性・未知DB拒否・途中失敗のロールバックを検証する。

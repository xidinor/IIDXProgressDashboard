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

`Initialize()` は出力ディレクトリを作成し、空のDBにv1・v2を順次適用する。既知のv1はバックアップ後にv2へ更新する。`OpenConnection()` は既存ファイルだけを開き、接続ごとに外部キーを有効にする。返した接続は呼び出し側がDisposeする。

現在のForm1への接続はPhase 6で行う。この段階ではアプリ起動時に新DBを作成しない。通常利用の保存場所も後続のUI設計で決定する。

## Migrationと既存DBの保全

- 即時トランザクション内でスキーマ検査・DDL・バージョン記録を行う。失敗時はDDLと履歴をまとめてロールバックする。作成済みの空ファイルやディレクトリは残る場合がある。
- 再実行時は、埋め込みDDLから作った各版の基準スキーマと全テーブル・インデックス等のSQL定義を照合し、Migration履歴が1から現在版まで連続していることを確認する。
- 旧DB、履歴欠落、将来バージョン、改変されたスキーマは変更せずエラーにする。SQL定義の厳密比較のため、外部ツールで同等のDDLへ書き換えたDBも拒否する。
- 2026-09-23：`002_external_song_ids.sql`を追加。v1からの更新前にはSQLiteのバックアップAPIでWALを含む確定済み状態を保存し、失敗時は更新しない。既存songs・charts・履歴の行は変更しない。
- `data/` は入力原本用。出力先に指定しない。Importer実装時には入力・出力の絶対パスの相違も検証する。

Phase 2-3では `DatabaseBackup.Create` を追加し、マスター更新前にSQLiteバックアップと整合性検査を行う。
保存先・保持方針・別パスへの復旧手順は[Phase 2-3解説](../docs/phase2-3-safe-master-update-explained.md)を参照。
Migration用のバックアップはDB隣接の`.pre-v2-<GUID>.bak`。版ごとの検査、未完了ファイルの区別、復旧は[外部IDのMigration解説](../docs/phase1-followup-external-song-ids-migration.md)を参照。

## 検証

```powershell
dotnet build IIDXProgressDashboard.sln
dotnet test tests/IIDXProgressDashboard.Tests/IIDXProgressDashboard.Tests.csproj
```

テストは一時ディレクトリの合成DBのみを使用する。新規作成・再実行・履歴保持・外部キー・一意制約・CHECK制約・別譜面とNORMAL/HARDの独立性・未知DB拒否・途中失敗のロールバックを検証する。

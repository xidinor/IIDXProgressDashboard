# Phase 1：DB基盤

対象：新DBの作成、Migration、バックアップ。整理時点：2026-09-30（Beta3ブランチ `d11abab`）。現行の実装説明をここから参照する。詳細な実施記録は末尾の「原記録」に残す。

## 実装とデータの流れ

```mermaid
flowchart LR
  C[呼出側] --> I[DatabaseInitializer]
  I --> M[MigrationRunner]
  M --> S[001 / 002 SQL]
  S --> D[(新形式DB)]
  D --> B[DatabaseBackup]
```

- [DatabaseInitializer](../Database/DatabaseInitializer.cs)は新規作成と既存DBの検査、接続ごとの外部キー有効化を担う。読取のための接続と初期化を分け、誤指定による空DB作成を避ける。
- [MigrationRunner](../Database/MigrationRunner.cs)は適用履歴と既知スキーマを確認し、未適用のMigrationを順に実行する。未知・新しい・不整合のあるスキーマを上書きしない。
- [001_initial.sql](../Database/Migrations/001_initial.sql)が曲・譜面・履歴・難易度表・取込監査の初期構造、[002_external_song_ids.sql](../Database/Migrations/002_external_song_ids.sql)が外部楽曲IDの追加を定義する。現在のDDLの正本はこの2ファイルとする。
- [DatabaseBackup](../Database/DatabaseBackup.cs)は非空DBの更新前にSQLiteバックアップを取る。失敗時には更新を始めない。復旧では稼働中DBと原本を保持し、別パスに戻して検査する。

`data/`の旧DBは入力・比較資料であり、Migrationの適用先ではない。新旧判定は名前でなくスキーマと`schema_migrations`による。外部IDの追加後も`songs.tag`、`charts.chart_id`と履歴の参照は維持する。

## 検証と残課題

初回作成、再実行、スキーマ拒否、外部キー・一意制約、Migration失敗時のロールバック、バックアップと参照保全は合成DBで検証した。過去の実行結果と手順は原記録を参照し、この文書整理でテストを再実行したとは扱わない。

[Issue #3](https://github.com/xidinor/IIDXProgressDashboard/issues/3)には通常保存先・INIとDBパスの一元化、起動時初期化、権限・ロックの案内、復旧の運用テストなどの未完了項目がある。Beta1～3は本体横のINIと準備済みDBを使うが、それをIssue #3全体の完了とはしない。配布環境での復旧も引き続き確認対象である。

## 原記録

- [Phase 1初期実装](phase1-database-explained.md)：初期化、Migration、テスト、当時の未実装範囲。
- [外部ID Migration](phase1-followup-external-song-ids-migration.md)：002の互換性、バックアップ、復旧。
- [Database/README](../Database/README.md)：DB基盤の利用と検証。
- [Issue #3](https://github.com/xidinor/IIDXProgressDashboard/issues/3)：後続の運用接続と未完了項目。

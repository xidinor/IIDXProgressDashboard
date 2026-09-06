# IIDXProgressDashboard 改善・統合仕様 v1.0

## 0. この文書の目的

既存の `IIDXProgressDashboard` を、以下の設計原則に従って改善する。

最終的な目的は、

> IIDX / INFINITAS の実際のプレイ履歴を蓄積し、その事実データからクリア状況、スコア推移、BP推移、難易度表ごとの進捗を算出・可視化するWindowsアプリケーション

とする。

既存実装を可能な範囲で活かしつつ、DBとデータ取込処理を再設計する。

---

# 1. 最重要設計原則

## 1.1 事実を保存し、集計値は算出する

DBには、

> 「その日時に、その譜面を、その条件でプレイし、その結果がどうだったか」

という事実を保存する。

以下は原則としてDBへ永続化せず、`play_history` から算出する。

- 現在の最高クリアランプ
- ベストスコア
- 最小BP
- 最終プレイ日時
- プレイ回数
- スコアレート
- DJ LEVEL
- 難易度表ごとのクリア曲数
- 難易度ランク別ランプ数

したがって、

```text
Score
1600
1540
1670
1590
1810
```

のようにスコアが上下する履歴もそのまま保存する。

BPも同様に、

```text
70
55
61
43
48
31
```

という変動をそのまま保持する。

---

## 1.2 1プレイ = play_history 1行

`play_history` は集約テーブルではない。

必ず、

```text
1 row = 1 actual play
```

とする。

外部データが「現在ベスト」しか提供しない場合、その値から架空のプレイ履歴を生成してはならない。

---

## 1.3 曲名を内部IDとして使用しない

曲は `songs.tag` で識別する。

譜面は内部的には `charts.chart_id` で識別する。

外部データとの標準的な譜面識別情報は、

```text
(tag, play_style, difficulty)
```

とする。

例:

```text
song tag + SP + A
song tag + SP + H
song tag + SP + L
```

曲名は外部データの照合にのみ使用する。

---

# 2. DB構成

すべての永続データを、

```text
iidx-progress.db
```

の1ファイルへ統合する。

旧構成の、

```text
infinitas_master.db
infinitas_log.db
```

は通常動作時には使用しない。

`infinitas_log.db` は旧履歴を一度だけ移行するためのImporter入力としてのみサポートする。

---

# 3. Python依存の廃止

現在の以下のPythonスクリプトは最終的に廃止する。

```text
python/buildsongmaster.py
python/convert_alllog_to_sqlite.py
python/scrape_lvl11.py
```

理由:

- WindowsでPython実行環境を別途準備したくない
- portable distributionを容易にしたい
- 可能な限りアプリケーションを1バイナリ化したい
- データ処理をC#へ統一したい

最終配布形態は概ね、

```text
IIDXProgressDashboard.exe
iidx-progress.db
```

とする。

.NET 8 self-contained / single-file publishを想定する。

Python Runtimeは要求しない。

---

# 4. v1で正式サポートするデータ入力

## 4.1 楽曲・譜面マスター

C#の `MasterDataProvider` を実装する。

現在の `buildsongmaster.py` が担当している、

- songs
- charts
- title
- artist
- genre
- version
- level
- total notes

等の取得・更新処理をC#へ移植する。

外部データ取得方式そのものはProvider内部へ閉じ込める。

---

## 4.2 非公式難易度表

`DifficultyTableProvider` を実装する。

少なくとも、

```text
SP ☆11 NORMAL
SP ☆11 HARD
SP ☆12 NORMAL
SP ☆12 HARD
```

を独立した難易度表として保持できる構造にする。

同一譜面がNORMALとHARDで別ランクでも問題なく保持できなければならない。

また、将来別ソースの難易度表を追加できる構造にする。

---

## 4.3 旧プレイ履歴

既存の、

```text
infinitas_log.db
```

を読み取る、

```text
LegacyInfinitasLogImporter
```

を実装する。

これは旧Pickleの内容を一度SQLite化したデータの移行用である。

移行完了後、通常利用では不要。

---

## 4.4 Reflux Session TSV

今後の標準的なプレイ履歴入力として、

```text
RefluxSessionTsvImporter
```

を実装する。

対象はRefluxの、

```text
Session_yyyy_MM_dd_HH_mm_ss.tsv
```

形式。

これは1プレイ1行の履歴データであり、`play_history` へ直接登録する。

---

# 5. v1では正式サポートしないもの

以下は初期実装では対応不要。

## 打鍵カウンタ best CSV

現在ベストのみで、実プレイ履歴ではないため正式サポート対象外とする。

## Reflux全曲best/tracker TSV

全曲の現在ベスト一覧であり、実プレイ履歴ではないため初期実装では対応不要。

将来必要になればBest Snapshot Importerとして追加可能。

## KONAMI公式CSV

将来的な入力元候補。

v1では未対応。

---

# 6. SQLite DDL v1

```sql
PRAGMA foreign_keys = ON;

BEGIN TRANSACTION;


-- ============================================================
-- Schema versions
-- ============================================================

CREATE TABLE schema_migrations (
    version             INTEGER PRIMARY KEY,
    description         TEXT NOT NULL,
    applied_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);


-- ============================================================
-- Import runs
-- ============================================================

CREATE TABLE import_runs (
    import_run_id       INTEGER PRIMARY KEY,

    source_type         TEXT NOT NULL,
    source_name         TEXT NOT NULL,

    source_path         TEXT,
    source_fingerprint  TEXT,

    options_json        TEXT,

    status              TEXT NOT NULL
                        CHECK (
                            status IN (
                                'RUNNING',
                                'SUCCESS',
                                'PARTIAL',
                                'FAILED'
                            )
                        ),

    records_read        INTEGER NOT NULL DEFAULT 0,
    records_imported    INTEGER NOT NULL DEFAULT 0,
    records_unresolved  INTEGER NOT NULL DEFAULT 0,

    started_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    completed_at        TEXT,

    message             TEXT
);

CREATE INDEX idx_import_runs_source
    ON import_runs(source_type, source_name);

CREATE INDEX idx_import_runs_fingerprint
    ON import_runs(source_fingerprint);


-- ============================================================
-- Songs
-- ============================================================

CREATE TABLE songs (
    tag                 TEXT PRIMARY KEY,

    title               TEXT NOT NULL,
    normalized_title    TEXT NOT NULL,

    artist              TEXT,
    genre               TEXT,
    version_name        TEXT,
    sort_index          INTEGER,

    is_active           INTEGER NOT NULL DEFAULT 1
                        CHECK (is_active IN (0, 1)),

    created_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_songs_title
    ON songs(title);

CREATE INDEX idx_songs_normalized_title
    ON songs(normalized_title);


-- ============================================================
-- Song aliases
-- ============================================================

CREATE TABLE song_aliases (
    alias_id            INTEGER PRIMARY KEY,

    tag                 TEXT NOT NULL,

    alias_title         TEXT NOT NULL,
    normalized_alias    TEXT NOT NULL,

    source_name         TEXT NOT NULL DEFAULT 'manual',
    note                TEXT,

    created_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,

    FOREIGN KEY(tag)
        REFERENCES songs(tag)
        ON UPDATE CASCADE
        ON DELETE RESTRICT,

    UNIQUE(source_name, normalized_alias)
);

CREATE INDEX idx_song_aliases_normalized
    ON song_aliases(normalized_alias);


-- ============================================================
-- Charts
-- ============================================================

CREATE TABLE charts (
    chart_id            INTEGER PRIMARY KEY,

    tag                 TEXT NOT NULL,

    play_style          TEXT NOT NULL
                        CHECK (
                            play_style IN ('SP', 'DP')
                        ),

    difficulty          TEXT NOT NULL
                        CHECK (
                            difficulty IN (
                                'B',
                                'N',
                                'H',
                                'A',
                                'L'
                            )
                        ),

    level               INTEGER
                        CHECK (
                            level IS NULL
                            OR level BETWEEN 1 AND 12
                        ),

    total_notes         INTEGER
                        CHECK (
                            total_notes IS NULL
                            OR total_notes >= 0
                        ),

    is_active           INTEGER NOT NULL DEFAULT 1
                        CHECK (is_active IN (0, 1)),

    created_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,

    FOREIGN KEY(tag)
        REFERENCES songs(tag)
        ON UPDATE CASCADE
        ON DELETE RESTRICT,

    UNIQUE(tag, play_style, difficulty)
);

CREATE INDEX idx_charts_tag
    ON charts(tag);

CREATE INDEX idx_charts_level_style
    ON charts(level, play_style);


-- ============================================================
-- Play history
--
-- 1 row = 1 actual play
-- ============================================================

CREATE TABLE play_history (
    play_id             INTEGER PRIMARY KEY,

    chart_id            INTEGER NOT NULL,

    played_at           TEXT NOT NULL,

    clear_lamp          INTEGER NOT NULL
                        CHECK (
                            clear_lamp BETWEEN 0 AND 7
                        ),

    score               INTEGER NOT NULL
                        CHECK (
                            score >= 0
                        ),

    miss_count          INTEGER
                        CHECK (
                            miss_count IS NULL
                            OR miss_count >= 0
                        ),

    gauge_percent       INTEGER
                        CHECK (
                            gauge_percent IS NULL
                            OR gauge_percent >= 0
                        ),

    pgreat              INTEGER
                        CHECK (
                            pgreat IS NULL
                            OR pgreat >= 0
                        ),

    great               INTEGER
                        CHECK (
                            great IS NULL
                            OR great >= 0
                        ),

    good                INTEGER
                        CHECK (
                            good IS NULL
                            OR good >= 0
                        ),

    bad                 INTEGER
                        CHECK (
                            bad IS NULL
                            OR bad >= 0
                        ),

    poor                INTEGER
                        CHECK (
                            poor IS NULL
                            OR poor >= 0
                        ),

    combo_break         INTEGER
                        CHECK (
                            combo_break IS NULL
                            OR combo_break >= 0
                        ),

    fast                INTEGER
                        CHECK (
                            fast IS NULL
                            OR fast >= 0
                        ),

    slow                INTEGER
                        CHECK (
                            slow IS NULL
                            OR slow >= 0
                        ),

    play_side           TEXT
                        CHECK (
                            play_side IS NULL
                            OR play_side IN (
                                'P1',
                                'P2',
                                'DP'
                            )
                        ),

    option_style_1      TEXT,
    option_style_2      TEXT,

    gauge_type          TEXT,
    assist_type         TEXT,
    range_type          TEXT,

    level_at_play       INTEGER
                        CHECK (
                            level_at_play IS NULL
                            OR level_at_play BETWEEN 1 AND 12
                        ),

    total_notes_at_play INTEGER
                        CHECK (
                            total_notes_at_play IS NULL
                            OR total_notes_at_play >= 0
                        ),

    source_system       TEXT NOT NULL,

    source_record_key   TEXT NOT NULL,

    import_run_id       INTEGER NOT NULL,

    raw_data            TEXT,

    imported_at         TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,

    FOREIGN KEY(chart_id)
        REFERENCES charts(chart_id)
        ON DELETE RESTRICT,

    FOREIGN KEY(import_run_id)
        REFERENCES import_runs(import_run_id)
        ON DELETE RESTRICT,

    UNIQUE(source_system, source_record_key)
);

CREATE INDEX idx_play_history_chart_date
    ON play_history(chart_id, played_at);

CREATE INDEX idx_play_history_date
    ON play_history(played_at);

CREATE INDEX idx_play_history_lamp
    ON play_history(chart_id, clear_lamp);

CREATE INDEX idx_play_history_score
    ON play_history(chart_id, score);

CREATE INDEX idx_play_history_bp
    ON play_history(chart_id, miss_count);


-- ============================================================
-- Difficulty tables
-- ============================================================

CREATE TABLE difficulty_tables (
    table_id            INTEGER PRIMARY KEY,

    table_code          TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL,

    level               INTEGER NOT NULL
                        CHECK (
                            level BETWEEN 1 AND 12
                        ),

    play_style          TEXT NOT NULL
                        CHECK (
                            play_style IN ('SP', 'DP')
                        ),

    gauge_type          TEXT NOT NULL
                        CHECK (
                            gauge_type IN (
                                'NORMAL',
                                'HARD'
                            )
                        ),

    source_name         TEXT NOT NULL,
    source_url          TEXT,
    source_revision     TEXT,

    is_active           INTEGER NOT NULL DEFAULT 1
                        CHECK (
                            is_active IN (0, 1)
                        ),

    updated_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_difficulty_tables_lookup
    ON difficulty_tables(
        level,
        play_style,
        gauge_type
    );


-- ============================================================
-- Difficulty ranks
-- ============================================================

CREATE TABLE difficulty_ranks (
    table_id            INTEGER NOT NULL,

    rank_code           TEXT NOT NULL,
    display_name        TEXT NOT NULL,

    rank_kind           TEXT NOT NULL
                        CHECK (
                            rank_kind IN (
                                'JIRIKI',
                                'KOJINSA',
                                'SPECIAL',
                                'UNDECIDED',
                                'OTHER'
                            )
                        ),

    sort_order          INTEGER NOT NULL,

    PRIMARY KEY (
        table_id,
        rank_code
    ),

    FOREIGN KEY(table_id)
        REFERENCES difficulty_tables(table_id)
        ON DELETE CASCADE
);

CREATE INDEX idx_difficulty_ranks_sort
    ON difficulty_ranks(
        table_id,
        sort_order DESC
    );


-- ============================================================
-- Difficulty table entries
-- ============================================================

CREATE TABLE difficulty_table_entries (
    table_id            INTEGER NOT NULL,

    chart_id            INTEGER NOT NULL,

    rank_code           TEXT NOT NULL,

    source_title        TEXT,
    source_difficulty   TEXT,

    import_run_id       INTEGER,

    updated_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (
        table_id,
        chart_id
    ),

    FOREIGN KEY(table_id)
        REFERENCES difficulty_tables(table_id)
        ON DELETE CASCADE,

    FOREIGN KEY(chart_id)
        REFERENCES charts(chart_id)
        ON DELETE RESTRICT,

    FOREIGN KEY(table_id, rank_code)
        REFERENCES difficulty_ranks(
            table_id,
            rank_code
        )
        ON DELETE RESTRICT,

    FOREIGN KEY(import_run_id)
        REFERENCES import_runs(import_run_id)
        ON DELETE SET NULL
);

CREATE INDEX idx_difficulty_entries_rank
    ON difficulty_table_entries(
        table_id,
        rank_code
    );


-- ============================================================
-- Unresolved imports
-- ============================================================

CREATE TABLE unresolved_imports (
    unresolved_id       INTEGER PRIMARY KEY,

    import_run_id       INTEGER NOT NULL,

    source_system       TEXT NOT NULL,
    source_record_key   TEXT,

    entity_type         TEXT NOT NULL,

    raw_song_name       TEXT,
    raw_difficulty_type TEXT,

    raw_level           INTEGER,
    raw_total_notes     INTEGER,

    reason_code         TEXT NOT NULL,
    reason_detail       TEXT,

    raw_data            TEXT,

    status              TEXT NOT NULL DEFAULT 'PENDING'
                        CHECK (
                            status IN (
                                'PENDING',
                                'RESOLVED',
                                'IGNORED'
                            )
                        ),

    resolved_chart_id   INTEGER,
    resolved_at         TEXT,

    created_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,

    FOREIGN KEY(import_run_id)
        REFERENCES import_runs(import_run_id)
        ON DELETE CASCADE,

    FOREIGN KEY(resolved_chart_id)
        REFERENCES charts(chart_id)
        ON DELETE SET NULL
);

CREATE INDEX idx_unresolved_status
    ON unresolved_imports(status);

CREATE INDEX idx_unresolved_title
    ON unresolved_imports(raw_song_name);


INSERT INTO schema_migrations (
    version,
    description
)
VALUES (
    1,
    'Initial unified database schema'
);

COMMIT;
```

---

# 7. クリアランプの内部表現

以下で統一する。

```text
0 = NO_PLAY
1 = FAILED
2 = ASSIST_CLEAR
3 = EASY_CLEAR
4 = CLEAR
5 = HARD_CLEAR
6 = EX_HARD_CLEAR
7 = FULL_COMBO
```

C# enumもこの値と一致させる。

RefluxのPFCは、

```text
FULL_COMBO = 7
```

として保存する。

PFC判定が必要であれば判定データから算出する。

---

# 8. NO PLAYの意味

`NO_PLAY` は、

> 一度もその譜面をプレイしたことがない

という意味ではない。

段位認定や旧AC Expertモードなどでは、

```text
clear_lamp = NO_PLAY
score > 0
```

という有効なプレイ記録が存在する。

したがって、

```text
clear_lamp == 0
```

を「未プレイ判定」に使用してはならない。

`play_history` に行が存在すれば、そのプレイは実際に発生したプレイとして扱う。

---

# 9. Chart Resolution

外部データを直接曲名でJOINしてはならない。

必ずImporter段階で `chart_id` を解決する。

基本フロー:

```text
external title
      ↓
TitleNormalizer
      ↓
songs.normalized_title
      ↓
必要なら song_aliases
      ↓
songs.tag
      ↓
external difficulty
      ↓
play_style + difficulty
      ↓
charts
      ↓
chart_id
```

例えば、

```text
SPA
```

は、

```text
play_style = SP
difficulty = A
```

へ変換する。

同様に、

```text
SPH → SP/H
SPL → SP/L
SPN → SP/N

DPA → DP/A
DPH → DP/H
DPL → DP/L
DPN → DP/N
```

とする。

---

# 10. level / total_notes の扱い

`level` と `total_notes` は譜面IDとして使用しない。

外部データから得られる場合は、

```text
level_at_play
total_notes_at_play
```

として保存する。

用途は、

- マスターとの整合性確認
- 誤マッチ検出
- 将来の譜面変更調査
- データソース異常の検出

とする。

例えば、

```text
AA
SPA
Lv12
1834 notes
```

をImporterが読み込んだ場合、

解決したchartが、

```text
SP/A
Lv12
1834 notes
```

であることを確認する。

不一致の場合、自動的に別譜面へ推測して登録してはならない。

---

# 11. unresolved_imports

譜面を安全に特定できないデータは破棄しない。

以下の場合は `unresolved_imports` へ登録する。

- 曲名が見つからない
- 正規化後も見つからない
- 複数候補になる
- difficultyが不正
- levelが矛盾
- total_notesが矛盾
- その他安全なchart_id決定ができない

Importerが推測だけで `chart_id` を決定してはならない。

---

# 12. Master update policy

マスター更新時に、

```sql
DELETE FROM songs;
DELETE FROM charts;
```

のような全削除を行ってはならない。

既存レコードはUPSERTする。

今回取得したマスターから消えた曲・譜面については、

```text
is_active = 0
```

とする。

理由:

過去の `play_history` が参照する譜面を削除してはならないため。

---

# 13. play_history の意味

以下を事実として保存する。

## 必須

```text
chart_id
played_at
clear_lamp
score
source_system
source_record_key
import_run_id
```

## 取得できる場合

```text
miss_count

gauge_percent

pgreat
great
good
bad
poor

combo_break

fast
slow

play_side

option_style_1
option_style_2

gauge_type
assist_type
range_type

level_at_play
total_notes_at_play
```

---

# 14. DBに保存しない算出値

## DJ LEVEL

保存しない。

scoreとtotal_notesから算出する。

---

## Score Rate

保存しない。

```text
score_rate =
score / (total_notes * 2) * 100
```

---

## ベストスコア

```sql
MAX(score)
```

---

## 最小BP

```sql
MIN(miss_count)
```

NULLは除外する。

---

## 最高クリアランプ

```sql
MAX(clear_lamp)
```

---

## 最終プレイ日時

```sql
MAX(played_at)
```

---

## プレイ回数

```sql
COUNT(*)
```

---

# 15. Reflux Session TSV Importer

クラス候補:

```text
RefluxSessionTsvImporter
```

固定列番号で解析してはならない。

Refluxは設定によりTSV列が増減するため、

> ヘッダ名で列を解決する

こと。

---

## 最低必須列

```text
title
difficulty
lamp
exscore
date
```

不足している場合、履歴Importerとして取り込みを開始しない。

---

## 任意列

```text
title2
bpm
artist
genre

notecount
level

playtype
grade
misscount

gaugepercent

pgreat
great
good
bad
poor
combobreak
fast
slow

style
style2
gauge
assist
range
```

---

# 16. Reflux列の対応

```text
title
    → chart resolution

difficulty
    → play_style / difficulty

notecount
    → total_notes_at_play

level
    → level_at_play

playtype
    → play_side

lamp
    → clear_lamp

misscount
    → miss_count

gaugepercent
    → gauge_percent

exscore
    → score

pgreat
    → pgreat

great
    → great

good
    → good

bad
    → bad

poor
    → poor

combobreak
    → combo_break

fast
    → fast

slow
    → slow

style
    → option_style_1

style2
    → option_style_2

gauge
    → gauge_type

assist
    → assist_type

range
    → range_type

date
    → played_at
```

`grade` は保存しない。

後から算出できるため。

---

# 17. miss_count

取得できない場合はNULL。

例えばRefluxで、

```text
-
```

となっている場合、

```text
NULL
```

へ変換する。

```text
0
```

はBP 0という有効値なのでNULLと混同してはならない。

---

# 18. 時刻

DBの `played_at` はUTCに正規化する。

推奨形式:

```text
YYYY-MM-DDTHH:MM:SSZ
```

例:

```text
2026-09-06T01:23:45Z
```

Refluxは設定によって、

```text
UTC
Local Time
```

のいずれでもTSV出力可能。

TSV自体にはタイムゾーン情報が含まれないため、Importerでは時刻解釈設定を持たせる。

初期値:

```text
UTC
```

必要ならImport UIで、

```text
UTC
Local
```

を選択可能にする。

使用した設定は `import_runs.options_json` へ残す。

---

# 19. Reflux重複Import防止

同じSession TSVを何度読み込んでも同じプレイを二重登録してはならない。

DBでは、

```sql
UNIQUE(source_system, source_record_key)
```

で防止する。

Refluxについては、単純な行内容SHAだけでは不十分。

完全に同一内容のプレイが複数回存在する可能性があるため。

安定したdedup keyを生成する。

推奨方式:

```text
rolling hash
```

例:

```text
key[0] =
SHA256(header + row0)

key[1] =
SHA256(key[0] + row1)

key[2] =
SHA256(key[1] + row2)
```

これにより、

- Sessionファイルへ後から行が追加されても過去行のkeyが変わらない
- 同一内容のプレイが連続しても別keyになる
- ファイルを再Importしても同一行を識別できる

`source_record_key` へ保存する。

---

# 20. LegacyInfinitasLogImporter

既存 `infinitas_log.db` の、

```text
id
level
song_name
difficulty_type
total_notes
clear_type
score
miss_count
played_option
played_at
original_data
```

を読み込む。

---

## 変換

```text
song_name
    → chart resolver

difficulty_type
    → SP/DP + B/N/H/A/L

level
    → level_at_play

total_notes
    → total_notes_at_play

clear_type
    → clear_lamp

score
    → score

miss_count
    → miss_count

played_option
    → raw_dataまたはoption補助情報

played_at
    → UTCへ変換

original_data
    → raw_data
```

旧データに存在しない、

```text
pgreat
great
good
bad
poor
fast
slow
gauge_percent
```

等はNULLでよい。

情報量の少ない過去履歴も完全に有効な履歴として扱う。

---

# 21. source_system

初期値として、

```text
LEGACY_INFINITAS_LOG
REFLUX_SESSION_TSV
```

を使用する。

将来的に、

```text
KONAMI_CSV
OTHER_TOOL
```

等を追加可能。

---

# 22. 難易度表モデル

難易度は「譜面そのものの属性」ではなく、

> ある難易度表におけるその譜面の評価

として扱う。

したがって、

```text
charts
```

へ非公式難易度を直接追加してはならない。

---

## 構造

```text
difficulty_tables
        │
        ├── difficulty_ranks
        │
        └── difficulty_table_entries
                          │
                          └── charts
```

これにより、

同一譜面が、

```text
☆11 NORMAL = 地力B
☆11 HARD   = 地力C
```

のように別評価でも保持できる。

別の難易度表ソースを将来追加することも可能。

---

# 23. UI側のデータ取得

現在のダッシュボードで行っている、

```text
song_name → strongest lamp
```

方式は廃止する。

必ず、

```text
chart_id
```

単位で集計する。

---

## 難易度表ランク別ランプ集計例

概念的には、

```sql
SELECT
    e.rank_code,
    ph.chart_id,
    MAX(ph.clear_lamp)
FROM difficulty_table_entries e
LEFT JOIN play_history ph
       ON ph.chart_id = e.chart_id
WHERE e.table_id = @table_id
GROUP BY
    e.rank_code,
    e.chart_id;
```

のようなchart単位集計を行い、その後ランク単位で件数を集計する。

---

# 24. 曲別履歴画面

既存の `oldmainform.cs` / `GraphForm` にあった、

- 曲検索
- 譜面選択
- スコア推移
- BP推移

は新DBでも維持・改善する。

検索後は必ず `chart_id` を確定し、

```sql
SELECT ...
FROM play_history
WHERE chart_id = @chartId
ORDER BY played_at;
```

とする。

曲名LIKEによる履歴直接検索を主処理にしてはならない。

---

# 25. 推奨C#構成

概念的には以下のように分離する。

```text
Database/
    DatabaseInitializer
    MigrationRunner
    Repositories/

Master/
    IMasterDataProvider
    MasterDataProvider

Import/
    IPlayHistoryImporter
    LegacyInfinitasLogImporter
    RefluxSessionTsvImporter

Matching/
    TitleNormalizer
    ChartResolver

Difficulty/
    IDifficultyTableProvider
    DifficultyTableProvider

Models/
    Song
    Chart
    PlayHistory
    DifficultyTable
    DifficultyRank
```

---

# 26. Importer共通責務

Importerは、

```text
Parse
 ↓
Normalize
 ↓
Resolve chart
 ↓
Validate
 ↓
Insert
```

の順とする。

Importer自身がUIロジックや集計ロジックを持たない。

---

# 27. 実装順序

## Phase 1

新DB基盤。

実装:

```text
001_initial.sql
DatabaseInitializer
MigrationRunner
```

---

## Phase 2

Master。

```text
MasterDataProvider
TitleNormalizer
ChartResolver
```

既存Python `buildsongmaster.py` の処理をC#化する。

---

## Phase 3

旧履歴移行。

```text
LegacyInfinitasLogImporter
```

既存 `infinitas_log.db` を新 `play_history` へ移行する。

---

## Phase 4

Reflux。

```text
RefluxSessionTsvImporter
```

Session TSVを正式な新規履歴入口にする。

---

## Phase 5

難易度表。

```text
DifficultyTableProvider
```

少なくとも、

```text
SP11 NORMAL
SP11 HARD
SP12 NORMAL
SP12 HARD
```

へ対応する。

---

## Phase 6

既存UIを新DBへ接続する。

対象:

```text
LampManager
DbHelper
Form1
GraphForm
```

---

## Phase 7

旧構成を削除する。

削除対象:

```text
infinitas_master.db 依存
infinitas_log.db 通常利用依存

python/buildsongmaster.py
python/convert_alllog_to_sqlite.py
python/scrape_lvl11.py
```

旧DB Importerそのものは移行機能として残してよい。

---

# 28. やってはいけないこと

以下は禁止する。

## song_nameだけでプレイ履歴を集計する

同名曲や別譜面混同の原因になる。

---

## HYPER / ANOTHER / LEGGENDARIAを同一曲として扱う

必ずchart単位。

---

## best CSVから架空のplay_historyを作る

実プレイではないため禁止。

---

## 不明な曲を「たぶんこれ」で登録する

`unresolved_imports` へ送る。

---

## master updateで過去chartをDELETEする

履歴参照が壊れるため禁止。

---

## DBに再計算可能な値を大量にキャッシュする

初期実装では事実データを優先する。

性能上必要になった場合のみ、VIEWや明示的なキャッシュ層を追加する。

---

# 29. v1完了条件

以下を満たした時点をDB/Importer再設計v1完了とする。

### DB

- `iidx-progress.db` のみで通常動作できる
- foreign keyが有効
- schema migrationが存在する

### Master

- songとchartが別管理される
- `(tag, play_style, difficulty)` が一意
- chart_idで全機能を関連付ける

### Legacy

- 既存 `infinitas_log.db` を取り込める
- 同一データを再Importしても重複しない

### Reflux

- Session TSVを取り込める
- Header構成の違いに対応する
- 同一Sessionを再Importしても重複しない
- scoreの上下を含む全プレイがそのまま保存される
- misscount `-` はNULLになる

### Difficulty

- ☆11 NORMAL/HARDを別テーブルとして保持できる
- ☆12 NORMAL/HARDを別テーブルとして保持できる
- H/A/Lを正しく区別する

### UI

- 難易度ランク集計がchart単位になる
- 曲別スコア履歴が表示できる
- BP履歴が表示できる
- 履歴は時系列で実プレイを表示する

### Runtime

- Python Runtime不要
- 旧2DB不要
- .NET self-contained single-file publish可能

---

# 30. 最終的なシステム像

```text
                External Master
                       │
                       ▼
               MasterDataProvider
                       │
                 songs / charts
                       │
       ┌───────────────┼────────────────┐
       │               │                │
       ▼               ▼                ▼
Legacy DB        Reflux Session     Difficulty Tables
Importer          TSV Importer          Provider
       │               │                │
       └───────┬───────┘                │
               ▼                        ▼
          play_history       difficulty_table_entries
               │                        │
               └───────────┬────────────┘
                           ▼
                     Application
                           │
          ┌────────────────┼───────────────┐
          ▼                ▼               ▼
     Lamp Progress    Score/BP Graph   Rank Progress
```

根幹となる考え方は、

> **外部からマスターと実際のプレイ事実を取得し、必要な状態・ベスト・進捗はその事実から算出する。**

これを今後のIIDXProgressDashboardの基本設計とする。
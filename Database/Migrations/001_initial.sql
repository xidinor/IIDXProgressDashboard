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

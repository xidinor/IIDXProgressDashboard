CREATE TABLE external_song_ids (
    source_name       TEXT NOT NULL DEFAULT 'IIDX_DATA_TABLE',
    external_song_id  INTEGER NOT NULL,

    title             TEXT NOT NULL,
    normalized_title  TEXT NOT NULL,

    tag               TEXT NOT NULL,

    is_active         INTEGER NOT NULL DEFAULT 1
                      CHECK (is_active IN (0, 1)),

    created_at        TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at        TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (
        source_name,
        external_song_id
    ),

    FOREIGN KEY(tag)
        REFERENCES songs(tag)
        ON UPDATE CASCADE
        ON DELETE RESTRICT
);

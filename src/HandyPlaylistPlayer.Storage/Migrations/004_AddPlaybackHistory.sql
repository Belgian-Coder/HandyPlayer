CREATE TABLE playback_history (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    media_file_id   INTEGER NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
    started_at      TEXT NOT NULL DEFAULT (datetime('now')),
    duration_ms     INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_playback_history_media ON playback_history(media_file_id);
CREATE INDEX idx_playback_history_started ON playback_history(started_at DESC);

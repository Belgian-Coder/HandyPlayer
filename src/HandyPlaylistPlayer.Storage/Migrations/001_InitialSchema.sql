CREATE TABLE library_roots (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    path        TEXT NOT NULL UNIQUE,
    label       TEXT,
    is_enabled  INTEGER NOT NULL DEFAULT 1,
    last_scan   TEXT,
    status      TEXT NOT NULL DEFAULT 'unknown'
);

CREATE TABLE media_files (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    library_root_id INTEGER NOT NULL REFERENCES library_roots(id) ON DELETE CASCADE,
    full_path       TEXT NOT NULL UNIQUE,
    filename        TEXT NOT NULL,
    extension       TEXT NOT NULL,
    file_size       INTEGER,
    modified_at     TEXT,
    duration_ms     INTEGER,
    is_script       INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX idx_media_files_filename ON media_files(filename);
CREATE INDEX idx_media_files_is_script ON media_files(is_script);
CREATE INDEX idx_media_files_root ON media_files(library_root_id);

CREATE TABLE pairings (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    video_file_id   INTEGER NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
    script_file_id  INTEGER NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
    is_manual       INTEGER NOT NULL DEFAULT 0,
    confidence      REAL NOT NULL DEFAULT 1.0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(video_file_id, script_file_id)
);

CREATE TABLE playlists (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL,
    type        TEXT NOT NULL DEFAULT 'static',
    folder_path TEXT,
    filter_json TEXT,
    sort_order  TEXT NOT NULL DEFAULT 'name',
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE playlist_items (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    playlist_id     INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    media_file_id   INTEGER NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
    position        INTEGER NOT NULL,
    UNIQUE(playlist_id, media_file_id)
);

CREATE TABLE device_profiles (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT NOT NULL,
    backend_type    TEXT NOT NULL,
    connection_key  TEXT,
    ws_url          TEXT,
    is_default      INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE presets (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    name                TEXT NOT NULL,
    device_profile_id   INTEGER REFERENCES device_profiles(id) ON DELETE SET NULL,
    playlist_id         INTEGER REFERENCES playlists(id) ON DELETE SET NULL,
    range_min           INTEGER NOT NULL DEFAULT 0,
    range_max           INTEGER NOT NULL DEFAULT 100,
    offset_ms           INTEGER NOT NULL DEFAULT 0,
    speed_limit         REAL,
    smoothing_enabled   INTEGER NOT NULL DEFAULT 1,
    smoothing_factor    REAL NOT NULL DEFAULT 0.3,
    invert              INTEGER NOT NULL DEFAULT 0,
    curve_gamma         REAL NOT NULL DEFAULT 1.0,
    tick_rate_ms        INTEGER NOT NULL DEFAULT 50,
    is_expert           INTEGER NOT NULL DEFAULT 0,
    created_at          TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE script_cache (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    sha256      TEXT NOT NULL UNIQUE,
    hosted_url  TEXT NOT NULL,
    uploaded_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE app_settings (
    key     TEXT PRIMARY KEY,
    value   TEXT NOT NULL
);

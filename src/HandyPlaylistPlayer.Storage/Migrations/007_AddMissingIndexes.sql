-- Foreign key indexes (SQLite doesn't auto-index FK columns)
CREATE INDEX IF NOT EXISTS idx_pairings_video_file ON pairings(video_file_id);
CREATE INDEX IF NOT EXISTS idx_pairings_script_file ON pairings(script_file_id);
CREATE INDEX IF NOT EXISTS idx_playlist_items_playlist ON playlist_items(playlist_id);
CREATE INDEX IF NOT EXISTS idx_playlist_items_media_file ON playlist_items(media_file_id);
CREATE INDEX IF NOT EXISTS idx_presets_device_profile ON presets(device_profile_id);
CREATE INDEX IF NOT EXISTS idx_presets_playlist ON presets(playlist_id);

-- Composite indexes for common query patterns
CREATE INDEX IF NOT EXISTS idx_pairings_video_manual_conf ON pairings(video_file_id, is_manual DESC, confidence DESC);
CREATE INDEX IF NOT EXISTS idx_playback_history_started_media ON playback_history(started_at DESC, media_file_id);

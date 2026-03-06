ALTER TABLE media_files ADD COLUMN file_hash TEXT;
CREATE INDEX idx_media_files_hash ON media_files(file_hash);

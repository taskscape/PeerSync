ALTER TABLE archive ADD COLUMN archive_path_key TEXT;
CREATE INDEX ix_archive_path ON archive(archive_path_key);
-- FolderIndex.OpenAsync performs invariant Unicode backfill in bounded
-- durable batches before exposing this upgraded database to engine callers.

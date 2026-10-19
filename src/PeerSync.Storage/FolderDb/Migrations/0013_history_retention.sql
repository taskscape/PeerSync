CREATE TABLE history_archive_links (
  marker_key TEXT PRIMARY KEY REFERENCES meta(key) ON DELETE CASCADE,
  source_id TEXT REFERENCES archive_provenance(source_id),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  file_id BLOB NOT NULL CHECK(length(file_id)=16),
  retained_ticks INTEGER NOT NULL CHECK(retained_ticks>0)
) WITHOUT ROWID;
CREATE INDEX ix_history_archive_source ON history_archive_links(source_id,retained_ticks);
CREATE TRIGGER history_archive_link_invalidate BEFORE UPDATE ON meta
WHEN OLD.key GLOB '_history-*' AND (OLD.key IS NOT NEW.key OR OLD.value IS NOT NEW.value) BEGIN
  DELETE FROM history_archive_links WHERE marker_key=OLD.key;
END;
CREATE TABLE archive_retirement_sources (
  retirement_id TEXT PRIMARY KEY REFERENCES archive_retirements(id) ON DELETE CASCADE,
  source_id TEXT NOT NULL REFERENCES archive_provenance(source_id)
) WITHOUT ROWID;
CREATE INDEX ix_retirement_source ON archive_retirement_sources(source_id);

CREATE TABLE archive_retirements (
  id TEXT PRIMARY KEY,
  archive_id INTEGER NOT NULL,
  original_path TEXT NOT NULL,
  archive_path TEXT NOT NULL,
  archived_ticks INTEGER NOT NULL,
  size INTEGER NOT NULL CHECK(size>=0),
  file_hash BLOB NOT NULL CHECK(length(file_hash)=32),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  file_id BLOB NOT NULL CHECK(length(file_id)=16),
  reason INTEGER NOT NULL CHECK(reason IN (1,2)),
  prepared_ticks INTEGER NOT NULL,
  completed_ticks INTEGER
) WITHOUT ROWID;
CREATE UNIQUE INDEX ix_archive_retirements_pending ON archive_retirements(archive_id) WHERE completed_ticks IS NULL;
CREATE INDEX ix_archive_retirements_completion ON archive_retirements(completed_ticks,id);

ALTER TABLE block_objects ADD COLUMN legacy_retention INTEGER NOT NULL DEFAULT 1 CHECK(legacy_retention IN (0,1));
CREATE TABLE block_retention_state (
  root_id BLOB PRIMARY KEY CHECK(length(root_id)=24),
  initialized_ticks INTEGER NOT NULL CHECK(initialized_ticks>0),
  legacy_until_ticks INTEGER NOT NULL CHECK(legacy_until_ticks>=initialized_ticks)
) WITHOUT ROWID;
CREATE TABLE block_orphans (
  hash BLOB PRIMARY KEY REFERENCES block_objects(hash) ON DELETE CASCADE,
  first_unreferenced_ticks INTEGER NOT NULL CHECK(first_unreferenced_ticks>0)
) WITHOUT ROWID;
CREATE INDEX ix_block_orphan_age ON block_orphans(first_unreferenced_ticks,hash);
CREATE TRIGGER block_orphan_registration AFTER UPDATE ON block_objects
WHEN OLD.root_id IS NOT NEW.root_id OR OLD.file_id IS NOT NEW.file_id OR OLD.length IS NOT NEW.length
  OR OLD.captured_ticks IS NOT NEW.captured_ticks OR OLD.last_used_ticks IS NOT NEW.last_used_ticks BEGIN
  DELETE FROM block_orphans WHERE hash=OLD.hash;
END;
CREATE TABLE block_deletions (
  id TEXT PRIMARY KEY CHECK(length(id)=32),
  hash BLOB NOT NULL UNIQUE CHECK(length(hash)=32),
  length INTEGER NOT NULL CHECK(length BETWEEN 1 AND 8388608),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  file_id BLOB NOT NULL CHECK(length(file_id)=16),
  captured_ticks INTEGER NOT NULL,
  last_used_ticks INTEGER NOT NULL,
  first_unreferenced_ticks INTEGER NOT NULL,
  prepared_ticks INTEGER NOT NULL,
  snapshot_id TEXT NOT NULL CHECK(length(snapshot_id)=32),
  reason INTEGER NOT NULL CHECK(reason IN (1,2))
) WITHOUT ROWID;

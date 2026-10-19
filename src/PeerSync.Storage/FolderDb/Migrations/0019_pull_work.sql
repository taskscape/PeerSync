ALTER TABLE needed ADD COLUMN work_stage INTEGER CHECK(work_stage BETWEEN 0 AND 3);
ALTER TABLE needed ADD COLUMN work_size INTEGER CHECK(work_size>=0);
ALTER TABLE needed ADD COLUMN work_mtime INTEGER CHECK(work_mtime BETWEEN 0 AND 3155378975999999999);
ALTER TABLE needed ADD COLUMN work_hash BLOB CHECK(work_hash IS NULL OR length(work_hash)=32);
CREATE INDEX ix_needed_work ON needed(work_stage,priority DESC,path_key);
CREATE TABLE remote_index_ranges (
  device_id BLOB PRIMARY KEY REFERENCES devices(device_id) ON DELETE CASCADE CHECK(length(device_id)=32),
  target_sequence INTEGER NOT NULL CHECK(target_sequence>=0)
) WITHOUT ROWID;
CREATE TABLE pull_retries (
  path_key TEXT PRIMARY KEY REFERENCES needed(path_key) ON DELETE CASCADE,
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  work_hash BLOB NOT NULL CHECK(length(work_hash)=32),
  attempts INTEGER NOT NULL CHECK(attempts BETWEEN 1 AND 7),
  failed_ticks INTEGER NOT NULL CHECK(failed_ticks>0),
  next_ticks INTEGER NOT NULL CHECK(next_ticks>=failed_ticks)
) WITHOUT ROWID;
CREATE TRIGGER needed_retry_invalidate AFTER UPDATE OF work_hash ON needed
WHEN OLD.work_hash IS NOT NEW.work_hash BEGIN
  DELETE FROM pull_retries WHERE path_key=NEW.path_key;
END;

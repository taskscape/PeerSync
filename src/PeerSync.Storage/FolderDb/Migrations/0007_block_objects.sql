CREATE TABLE block_objects (
  hash BLOB PRIMARY KEY CHECK(length(hash)=32),
  length INTEGER NOT NULL CHECK(length BETWEEN 1 AND 8388608),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  file_id BLOB NOT NULL CHECK(length(file_id)=16),
  captured_ticks INTEGER NOT NULL,
  last_used_ticks INTEGER NOT NULL
) WITHOUT ROWID;
CREATE INDEX ix_block_objects_age ON block_objects(last_used_ticks,hash);
UPDATE local_files SET content_ready=0 WHERE type=0 AND deleted=0 AND record_body IS NOT NULL;

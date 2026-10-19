CREATE TABLE staging_retirements (
  staged_id TEXT PRIMARY KEY REFERENCES staged_objects(id) ON DELETE CASCADE,
  prepared_ticks INTEGER NOT NULL CHECK(prepared_ticks BETWEEN 1 AND 3155378975999999999)
) WITHOUT ROWID;
CREATE TABLE staging_cleanup_state (
  root_id BLOB PRIMARY KEY CHECK(length(root_id)=24),
  last_id TEXT
) WITHOUT ROWID;
CREATE INDEX ix_staging_cursor ON staged_objects(root_id,state,id);

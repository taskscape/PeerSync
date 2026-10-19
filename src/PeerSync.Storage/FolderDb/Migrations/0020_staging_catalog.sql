CREATE TABLE staged_objects (
  serial INTEGER PRIMARY KEY AUTOINCREMENT,
  id TEXT NOT NULL UNIQUE,
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  file_id BLOB NOT NULL CHECK(length(file_id)=16),
  path_key TEXT NOT NULL,
  target_path TEXT NOT NULL,
  temp_path TEXT NOT NULL,
  temp_key TEXT NOT NULL,
  kind INTEGER NOT NULL CHECK(kind IN (0,1)),
  parked_ticks INTEGER NOT NULL CHECK(parked_ticks BETWEEN 1 AND 3155378975999999999),
  file_length INTEGER NOT NULL CHECK(file_length>=0),
  content_hash BLOB,
  change_filetime INTEGER NOT NULL CHECK(change_filetime>=0),
  state INTEGER NOT NULL DEFAULT 0 CHECK(state BETWEEN 0 AND 2),
  CHECK((kind=0 AND content_hash IS NOT NULL AND length(content_hash)=32) OR (kind=1 AND content_hash IS NULL AND file_length=0)),
  UNIQUE(root_id,file_id,temp_key)
);
CREATE INDEX ix_staging_reuse ON staged_objects(root_id,path_key,kind,state,serial DESC);
CREATE INDEX ix_staging_expiry ON staged_objects(root_id,state,parked_ticks,serial);

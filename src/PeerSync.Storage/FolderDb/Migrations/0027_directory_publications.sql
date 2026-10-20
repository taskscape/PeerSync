CREATE TABLE directory_publications (
  path_key TEXT PRIMARY KEY,
  path TEXT NOT NULL,
  parent_key TEXT NOT NULL,
  depth INTEGER NOT NULL CHECK(depth BETWEEN 0 AND 2047),
  version TEXT NOT NULL CHECK(length(version)=32),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  native_id BLOB NOT NULL CHECK(length(native_id)=16),
  expected_local_seq INTEGER NOT NULL CHECK(expected_local_seq>=0),
  attributes INTEGER NOT NULL CHECK(attributes BETWEEN 0 AND 4294967295),
  record_body BLOB NOT NULL CHECK(length(record_body) BETWEEN 1 AND 16777216),
  signature BLOB NOT NULL CHECK(length(signature)=64),
  expected_keys_hash BLOB NOT NULL CHECK(length(expected_keys_hash)=32),
  expected_children INTEGER NOT NULL CHECK(expected_children>=0),
  keys_epoch INTEGER CHECK(keys_epoch IS NULL OR keys_epoch>=0),
  paused INTEGER NOT NULL DEFAULT 0 CHECK(paused IN (0,1)),
  attempts INTEGER NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 7),
  failed_ticks INTEGER,
  next_ticks INTEGER,
  created_ticks INTEGER NOT NULL CHECK(created_ticks BETWEEN 1 AND 3155378975999999999),
  touched_ticks INTEGER NOT NULL CHECK(touched_ticks BETWEEN 1 AND 3155378975999999999),
  CHECK((attempts=0 AND failed_ticks IS NULL AND next_ticks IS NULL)
    OR (attempts>0 AND failed_ticks BETWEEN 1 AND 3155378975999999999 AND next_ticks BETWEEN failed_ticks AND 3155378975999999999))
) WITHOUT ROWID;
CREATE INDEX ix_directory_publication_parent ON directory_publications(parent_key,path_key);
CREATE INDEX ix_directory_publication_due ON directory_publications(paused,depth DESC,path_key);
CREATE INDEX ix_directory_publication_expiry ON directory_publications(root_id,touched_ticks,path_key);
CREATE TABLE directory_publication_cache (
  path_key TEXT PRIMARY KEY,
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  native_id BLOB NOT NULL CHECK(length(native_id)=16),
  record_source_id BLOB NOT NULL CHECK(length(record_source_id)=32),
  input_hash BLOB NOT NULL CHECK(length(input_hash)=32),
  expected_keys_hash BLOB NOT NULL CHECK(length(expected_keys_hash)=32),
  member_count INTEGER NOT NULL CHECK(member_count>=0),
  input_epoch INTEGER NOT NULL CHECK(input_epoch>=0),
  touched_ticks INTEGER NOT NULL CHECK(touched_ticks BETWEEN 1 AND 3155378975999999999)
) WITHOUT ROWID;
CREATE INDEX ix_directory_publication_cache_expiry ON directory_publication_cache(root_id,touched_ticks,path_key);

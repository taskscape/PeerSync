CREATE TABLE directory_captures (
  parent_key TEXT PRIMARY KEY,
  parent_path TEXT NOT NULL,
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  native_id BLOB NOT NULL CHECK(length(native_id)=16),
  input_hash BLOB NOT NULL CHECK(length(input_hash)=32),
  member_count INTEGER NOT NULL CHECK(member_count>=0),
  accepted INTEGER NOT NULL DEFAULT 0 CHECK(accepted BETWEEN 0 AND member_count),
  last_key TEXT,
  builder_state BLOB CHECK(builder_state IS NULL OR length(builder_state) BETWEEN 16 AND 50328),
  node_hash BLOB CHECK(node_hash IS NULL OR length(node_hash)=32),
  node_length INTEGER CHECK(node_length IS NULL OR (node_length BETWEEN 12 AND 4620 AND (node_length-12)%36=0)),
  last_used_ticks INTEGER NOT NULL CHECK(last_used_ticks BETWEEN 1 AND 3155378975999999999),
  CHECK((accepted=0 AND last_key IS NULL) OR (accepted>0 AND last_key IS NOT NULL)),
  CHECK((node_hash IS NULL AND node_length IS NULL AND builder_state IS NOT NULL)
    OR (node_hash IS NOT NULL AND node_length IS NOT NULL AND builder_state IS NULL AND accepted=member_count))
) WITHOUT ROWID;
CREATE INDEX ix_directory_capture_expiry ON directory_captures(root_id,last_used_ticks,parent_key);

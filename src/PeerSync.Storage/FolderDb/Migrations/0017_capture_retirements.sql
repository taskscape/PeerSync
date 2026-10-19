CREATE TABLE capture_retirements (
  id TEXT PRIMARY KEY CHECK(length(id)=32),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  marker_id BLOB NOT NULL CHECK(length(marker_id)=16),
  data_id BLOB NOT NULL CHECK(length(data_id)=16),
  expected_hash BLOB NOT NULL CHECK(length(expected_hash)=32),
  expected_length INTEGER NOT NULL CHECK(expected_length BETWEEN 1 AND 8388608),
  captured_ticks INTEGER NOT NULL CHECK(captured_ticks>0),
  prefix_length INTEGER NOT NULL CHECK(prefix_length>=0 AND prefix_length<expected_length),
  prefix_hash BLOB NOT NULL CHECK(length(prefix_hash)=32),
  prepared_ticks INTEGER NOT NULL CHECK(prepared_ticks>=captured_ticks),
  snapshot_id TEXT NOT NULL CHECK(length(snapshot_id)=32),
  replacement_id BLOB CHECK(replacement_id IS NULL OR length(replacement_id)=16)
) WITHOUT ROWID;

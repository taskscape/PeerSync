CREATE TABLE case_preservations (
  source_id BLOB PRIMARY KEY CHECK(length(source_id)=32),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  path_key TEXT NOT NULL,
  source_device BLOB NOT NULL CHECK(length(source_device)=32),
  record_body BLOB NOT NULL CHECK(length(record_body) BETWEEN 1 AND 16777216),
  signature BLOB NOT NULL CHECK(length(signature)=64),
  completed INTEGER NOT NULL DEFAULT 0 CHECK(completed IN (0,1)),
  destination_key TEXT,
  destination_path TEXT,
  installed_file_id BLOB CHECK(installed_file_id IS NULL OR length(installed_file_id)=16)
) WITHOUT ROWID;
CREATE INDEX ix_case_preservations_pending ON case_preservations(completed,source_id);
CREATE INDEX ix_case_preservations_path ON case_preservations(path_key,completed);
CREATE INDEX ix_case_preservations_destination ON case_preservations(destination_key,completed);

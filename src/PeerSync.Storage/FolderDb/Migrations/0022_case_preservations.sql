CREATE TABLE case_preservations (
  source_id BLOB PRIMARY KEY CHECK(length(source_id)=32),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  path_key TEXT NOT NULL,
  source_device BLOB CHECK(source_device IS NULL OR length(source_device)=32),
  record_body BLOB CHECK(record_body IS NULL OR length(record_body) BETWEEN 1 AND 16777216),
  signature BLOB CHECK(signature IS NULL OR length(signature)=64),
  completed INTEGER NOT NULL DEFAULT 0 CHECK(completed IN (0,1)),
  completed_ticks INTEGER CHECK(completed_ticks IS NULL OR completed_ticks BETWEEN 1 AND 3155378975999999999),
  destination_key TEXT,
  destination_path TEXT,
  installed_file_id BLOB CHECK(installed_file_id IS NULL OR length(installed_file_id)=16),
  CHECK(completed=0 OR completed_ticks IS NOT NULL),
  CHECK((source_device IS NOT NULL AND record_body IS NOT NULL AND signature IS NOT NULL)
    OR (completed=1 AND source_device IS NULL AND record_body IS NULL AND signature IS NULL))
) WITHOUT ROWID;
CREATE INDEX ix_case_preservations_pending ON case_preservations(completed,source_id);
CREATE INDEX ix_case_preservations_path ON case_preservations(path_key,completed);
CREATE INDEX ix_case_preservations_destination ON case_preservations(destination_key,completed);
CREATE INDEX ix_case_preservations_compaction ON case_preservations(completed,completed_ticks,source_id);
CREATE TABLE case_preservation_retries (
  source_id BLOB PRIMARY KEY REFERENCES case_preservations(source_id) ON DELETE CASCADE,
  attempts INTEGER NOT NULL CHECK(attempts BETWEEN 1 AND 7),
  failed_ticks INTEGER NOT NULL CHECK(failed_ticks BETWEEN 1 AND 3155378975999999999),
  next_ticks INTEGER NOT NULL CHECK(next_ticks BETWEEN failed_ticks AND 3155378975999999999)
) WITHOUT ROWID;

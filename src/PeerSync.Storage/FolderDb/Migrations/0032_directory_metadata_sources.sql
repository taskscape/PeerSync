CREATE TABLE directory_metadata_sources (
  source_id BLOB PRIMARY KEY CHECK(length(source_id)=32),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  path TEXT NOT NULL CHECK(length(path) BETWEEN 1 AND 4096),
  record_body BLOB NOT NULL CHECK(length(record_body) BETWEEN 1 AND 16777216),
  signature BLOB NOT NULL CHECK(length(signature)=64),
  node_hash BLOB NOT NULL CHECK(length(node_hash)=32),
  node_length INTEGER NOT NULL CHECK(node_length BETWEEN 12 AND 4620 AND (node_length-12)%36=0),
  route_hint BLOB NOT NULL CHECK(length(route_hint)=32),
  requested_ticks INTEGER NOT NULL CHECK(requested_ticks BETWEEN 1 AND 3155378975999999999),
  run_order INTEGER NOT NULL DEFAULT 0 CHECK(typeof(run_order)='integer' AND run_order>=0),
  attempts INTEGER NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 7),
  failed_ticks INTEGER,
  next_ticks INTEGER,
  route_after BLOB CHECK(route_after IS NULL OR length(route_after)=32),
  CHECK((failed_ticks IS NULL)=(next_ticks IS NULL)),
  CHECK((failed_ticks IS NULL AND next_ticks IS NULL) OR (failed_ticks BETWEEN 1 AND 3155378975999999999 AND next_ticks BETWEEN failed_ticks AND 3155378975999999999))
) WITHOUT ROWID;
CREATE INDEX ix_directory_metadata_due ON directory_metadata_sources(root_id,run_order,source_id);
CREATE INDEX ix_directory_metadata_age ON directory_metadata_sources(root_id,requested_ticks,source_id);
CREATE TABLE directory_metadata_schedule (
  singleton INTEGER PRIMARY KEY CHECK(singleton=1),
  last_order INTEGER NOT NULL CHECK(typeof(last_order)='integer' AND last_order>=0)
);
INSERT INTO directory_metadata_schedule(singleton,last_order) VALUES(1,0);
CREATE INDEX ix_case_metadata_source_cursor ON case_preservations(root_id,source_id) WHERE completed=1 AND retained_until_ticks IS NOT NULL AND record_body IS NOT NULL;

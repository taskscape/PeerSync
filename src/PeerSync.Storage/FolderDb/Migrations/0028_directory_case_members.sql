CREATE TABLE directory_case_progress (
  source_id BLOB NOT NULL REFERENCES case_preservations(source_id),
  root_file_id BLOB NOT NULL CHECK(length(root_file_id)=16),
  last_key TEXT,
  exhausted INTEGER NOT NULL DEFAULT 0 CHECK(exhausted IN (0,1)),
  PRIMARY KEY(source_id,root_file_id)
) WITHOUT ROWID;
CREATE TABLE directory_case_members (
  source_id BLOB NOT NULL REFERENCES case_preservations(source_id),
  root_file_id BLOB NOT NULL CHECK(length(root_file_id)=16),
  member_key TEXT NOT NULL,
  record_hash BLOB NOT NULL CHECK(length(record_hash)=32),
  record_length INTEGER NOT NULL CHECK(record_length BETWEEN 48 AND 624),
  member_source_id BLOB NOT NULL CHECK(length(member_source_id)=32),
  type INTEGER NOT NULL CHECK(type IN (0,1)),
  destination_path TEXT NOT NULL,
  installed_file_id BLOB CHECK(installed_file_id IS NULL OR length(installed_file_id)=16),
  touched_ticks INTEGER NOT NULL CHECK(touched_ticks BETWEEN 1 AND 3155378975999999999),
  PRIMARY KEY(source_id,root_file_id,member_key)
) WITHOUT ROWID;

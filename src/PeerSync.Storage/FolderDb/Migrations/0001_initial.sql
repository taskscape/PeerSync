CREATE TABLE local_files (
  path_key TEXT PRIMARY KEY,
  path TEXT NOT NULL,
  type INTEGER NOT NULL,
  size INTEGER NOT NULL,
  mtime_ticks INTEGER NOT NULL,
  attributes INTEGER NOT NULL,
  deleted INTEGER NOT NULL DEFAULT 0,
  changed_ticks INTEGER NOT NULL,
  file_hash BLOB,
  vv BLOB NOT NULL,
  record_body BLOB,
  signature BLOB,
  ntfs_file_id BLOB,
  local_seq INTEGER NOT NULL UNIQUE,
  flags INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE remote_files (
  device_id BLOB NOT NULL,
  path_key TEXT NOT NULL,
  vv BLOB NOT NULL,
  record_body BLOB NOT NULL,
  signature BLOB NOT NULL,
  PRIMARY KEY (device_id, path_key)
) WITHOUT ROWID;
CREATE TABLE needed (
  path_key TEXT PRIMARY KEY,
  source_device BLOB NOT NULL,
  priority INTEGER NOT NULL
);
CREATE TABLE chunks (
  hash BLOB NOT NULL,
  path_key TEXT NOT NULL,
  file_offset INTEGER NOT NULL,
  length INTEGER NOT NULL,
  PRIMARY KEY (hash, path_key, file_offset)
) WITHOUT ROWID;
CREATE TABLE devices (
  device_id BLOB PRIMARY KEY,
  name TEXT,
  last_seen_ticks INTEGER,
  last_seq_received INTEGER NOT NULL DEFAULT 0,
  approval INTEGER NOT NULL DEFAULT 0,
  pinned INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE archive (
  id INTEGER PRIMARY KEY,
  original_path TEXT NOT NULL,
  archive_path TEXT NOT NULL,
  archived_ticks INTEGER NOT NULL,
  size INTEGER NOT NULL,
  file_hash BLOB
);
CREATE INDEX ix_archive_time ON archive(archived_ticks);
CREATE TABLE meta (key TEXT PRIMARY KEY, value BLOB);

CREATE TABLE folders (
  id BLOB NOT NULL PRIMARY KEY CHECK(length(id)=32),
  path TEXT NOT NULL,
  path_key TEXT NOT NULL UNIQUE,
  access_level INTEGER NOT NULL CHECK(access_level BETWEEN 0 AND 2),
  protected_key BLOB NOT NULL,
  settings_json TEXT NOT NULL,
  paused INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE devices (
  device_id BLOB PRIMARY KEY,
  name TEXT,
  last_seen_ticks INTEGER,
  last_seq_received INTEGER NOT NULL DEFAULT 0,
  approval INTEGER NOT NULL DEFAULT 0,
  pinned INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE pending_approvals (
  folder_id BLOB NOT NULL,
  device_id BLOB NOT NULL CHECK(length(device_id)=32),
  name TEXT,
  requested_ticks INTEGER NOT NULL,
  PRIMARY KEY(folder_id,device_id),
  FOREIGN KEY(folder_id) REFERENCES folders(id) ON DELETE CASCADE
);
CREATE TABLE audit_log (
  id INTEGER PRIMARY KEY,
  occurred_ticks INTEGER NOT NULL,
  action TEXT NOT NULL,
  actor_sid TEXT NOT NULL,
  folder_id BLOB,
  device_id BLOB
);
CREATE INDEX ix_audit_time ON audit_log(occurred_ticks);

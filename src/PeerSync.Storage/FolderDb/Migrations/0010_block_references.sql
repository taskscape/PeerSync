CREATE TABLE block_reference_snapshots (
  id TEXT PRIMARY KEY,
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  created_ticks INTEGER NOT NULL,
  active INTEGER NOT NULL CHECK(active IN (0,1))
) WITHOUT ROWID;
CREATE TABLE block_reference_archives (
  snapshot_id TEXT NOT NULL REFERENCES block_reference_snapshots(id) ON DELETE CASCADE,
  archive_id INTEGER NOT NULL,
  PRIMARY KEY(snapshot_id,archive_id)
) WITHOUT ROWID;
CREATE UNIQUE INDEX ix_block_references_active ON block_reference_snapshots(active) WHERE active=1;
CREATE TABLE block_references (
  snapshot_id TEXT NOT NULL REFERENCES block_reference_snapshots(id) ON DELETE CASCADE,
  hash BLOB NOT NULL CHECK(length(hash)=32),
  length INTEGER NOT NULL CHECK(length BETWEEN 1 AND 8388608),
  kinds INTEGER NOT NULL CHECK(kinds BETWEEN 1 AND 15),
  PRIMARY KEY(snapshot_id,hash)
) WITHOUT ROWID;

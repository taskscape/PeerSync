CREATE TABLE archive_contents (
  id TEXT PRIMARY KEY,
  source_id TEXT NOT NULL REFERENCES archive_provenance(source_id),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  file_id BLOB NOT NULL CHECK(length(file_id)=16),
  size INTEGER NOT NULL CHECK(size>=0),
  file_hash BLOB NOT NULL CHECK(length(file_hash)=32),
  verified_ticks INTEGER NOT NULL,
  active INTEGER NOT NULL CHECK(active IN (0,1)),
  next_offset INTEGER NOT NULL DEFAULT 0 CHECK(next_offset>=0 AND next_offset<=size),
  chunk_count INTEGER NOT NULL DEFAULT 0 CHECK(chunk_count BETWEEN 0 AND 100000)
) WITHOUT ROWID;
CREATE UNIQUE INDEX ix_archive_contents_active ON archive_contents(source_id) WHERE active=1;
CREATE INDEX ix_archive_contents_source ON archive_contents(source_id,active);
CREATE TABLE archive_content_chunks (
  content_id TEXT NOT NULL REFERENCES archive_contents(id) ON DELETE CASCADE,
  file_offset INTEGER NOT NULL CHECK(file_offset>=0),
  hash BLOB NOT NULL CHECK(length(hash)=32),
  length INTEGER NOT NULL CHECK(length BETWEEN 1 AND 8388608),
  PRIMARY KEY(content_id,file_offset)
) WITHOUT ROWID;
CREATE TABLE block_reference_sources (
  snapshot_id TEXT NOT NULL REFERENCES block_reference_snapshots(id) ON DELETE CASCADE,
  source_id TEXT NOT NULL REFERENCES archive_provenance(source_id),
  PRIMARY KEY(snapshot_id,source_id)
) WITHOUT ROWID;
INSERT INTO block_reference_sources
  SELECT c.snapshot_id,p.source_id FROM block_reference_archives c
  JOIN archive_provenance p ON p.archive_id=c.archive_id
  JOIN block_reference_snapshots s ON s.id=c.snapshot_id
  WHERE p.root_id IS NULL OR p.root_id=s.root_id;

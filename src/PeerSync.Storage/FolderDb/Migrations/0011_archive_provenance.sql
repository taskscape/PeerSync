-- Local protection metadata survives physical archive-row reconciliation.
-- It is not native ownership, a content manifest, or deletion authorization.
CREATE TABLE archive_provenance (
  source_id TEXT PRIMARY KEY CHECK(length(source_id)=32),
  archive_id INTEGER UNIQUE REFERENCES archive(id) ON DELETE SET NULL,
  original_archive_id INTEGER NOT NULL,
  original_path TEXT NOT NULL,
  archive_path TEXT NOT NULL,
  archived_ticks INTEGER NOT NULL,
  size INTEGER NOT NULL CHECK(size>=0),
  file_hash BLOB CHECK(file_hash IS NULL OR length(file_hash)=32),
  root_id BLOB CHECK(root_id IS NULL OR length(root_id)=24)
) WITHOUT ROWID;
CREATE INDEX ix_archive_provenance_version ON archive_provenance(original_path,file_hash,size);
INSERT INTO archive_provenance
  SELECT lower(hex(randomblob(16))),id,id,original_path,archive_path,archived_ticks,size,file_hash,
    (SELECT value FROM meta WHERE key='_root-identity') FROM archive;

CREATE TRIGGER archive_provenance_insert AFTER INSERT ON archive BEGIN
  INSERT INTO archive_provenance VALUES(lower(hex(randomblob(16))),NEW.id,NEW.id,
    NEW.original_path,NEW.archive_path,NEW.archived_ticks,NEW.size,NEW.file_hash,
    (SELECT value FROM meta WHERE key='_root-identity'));
END;

CREATE TRIGGER archive_provenance_delete BEFORE DELETE ON archive BEGIN
  UPDATE archive_provenance SET archive_id=NULL WHERE archive_id=OLD.id;
END;

CREATE TRIGGER archive_provenance_before_update BEFORE UPDATE ON archive
WHEN OLD.id IS NOT NEW.id OR OLD.original_path IS NOT NEW.original_path
  OR OLD.archive_path IS NOT NEW.archive_path OR OLD.archived_ticks IS NOT NEW.archived_ticks
  OR OLD.size IS NOT NEW.size OR OLD.file_hash IS NOT NEW.file_hash BEGIN
  UPDATE archive_provenance SET archive_id=NULL WHERE archive_id=OLD.id;
END;

CREATE TRIGGER archive_provenance_after_update AFTER UPDATE ON archive
WHEN OLD.id IS NOT NEW.id OR OLD.original_path IS NOT NEW.original_path
  OR OLD.archive_path IS NOT NEW.archive_path OR OLD.archived_ticks IS NOT NEW.archived_ticks
  OR OLD.size IS NOT NEW.size OR OLD.file_hash IS NOT NEW.file_hash BEGIN
  INSERT INTO archive_provenance VALUES(lower(hex(randomblob(16))),NEW.id,NEW.id,
    NEW.original_path,NEW.archive_path,NEW.archived_ticks,NEW.size,NEW.file_hash,
    (SELECT value FROM meta WHERE key='_root-identity'));
END;

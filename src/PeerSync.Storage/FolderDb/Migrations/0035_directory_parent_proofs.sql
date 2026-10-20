CREATE TABLE directory_parent_proofs (
  path_key TEXT PRIMARY KEY,
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  native_id BLOB NOT NULL CHECK(length(native_id)=16),
  metadata_hash BLOB NOT NULL CHECK(length(metadata_hash)=32),
  input_hash BLOB NOT NULL CHECK(length(input_hash)=32),
  input_epoch INTEGER NOT NULL CHECK(typeof(input_epoch)='integer' AND input_epoch>=0),
  child_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(child_epoch)='integer' AND child_epoch>=0),
  verified_child_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(verified_child_epoch)='integer' AND verified_child_epoch>=0),
  remote_revision INTEGER NOT NULL DEFAULT 0 CHECK(typeof(remote_revision)='integer' AND remote_revision>=0),
  discovery_revision INTEGER NOT NULL DEFAULT 0 CHECK(typeof(discovery_revision)='integer' AND discovery_revision>=0),
  generation INTEGER NOT NULL DEFAULT 1 CHECK(typeof(generation)='integer' AND generation>0),
  discovery_after BLOB CHECK(discovery_after IS NULL OR length(discovery_after)=32),
  discovery_clock BLOB NOT NULL DEFAULT X'00',
  discovery_complete INTEGER NOT NULL DEFAULT 0 CHECK(discovery_complete IN (0,1)),
  resetting INTEGER NOT NULL DEFAULT 0 CHECK(resetting IN (0,1)),
  queue_version TEXT NOT NULL,
  local_seq INTEGER NOT NULL CHECK(local_seq>=0),
  local_source_id BLOB NOT NULL CHECK(length(local_source_id)=32),
  local_hash BLOB NOT NULL CHECK(length(local_hash)=32),
  local_length INTEGER NOT NULL CHECK(local_length>=12 AND local_length<=4620 AND (local_length-12)%36=0),
  touched_ticks INTEGER NOT NULL CHECK(touched_ticks>0)
) WITHOUT ROWID;
CREATE INDEX ix_directory_parent_proof_age ON directory_parent_proofs(touched_ticks,path_key);
CREATE INDEX ix_directory_parent_proof_root ON directory_parent_proofs(root_id,path_key);
CREATE TABLE directory_parent_roots (
  path_key TEXT NOT NULL REFERENCES directory_parent_proofs(path_key) ON DELETE CASCADE,
  node_hash BLOB NOT NULL CHECK(length(node_hash)=32),
  node_length INTEGER NOT NULL CHECK(node_length>=12 AND node_length<=4620 AND (node_length-12)%36=0),
  source_id BLOB NOT NULL UNIQUE CHECK(length(source_id)=32),
  source_path TEXT NOT NULL,
  source_device BLOB NOT NULL CHECK(length(source_device)=32),
  record_body BLOB NOT NULL,
  signature BLOB NOT NULL CHECK(length(signature)=64),
  seen_generation INTEGER NOT NULL CHECK(seen_generation>0),
  cursor TEXT,
  proof_input_hash BLOB CHECK(proof_input_hash IS NULL OR length(proof_input_hash)=32),
  proof_child_epoch INTEGER NOT NULL DEFAULT -1 CHECK(proof_child_epoch>=-1),
  complete INTEGER NOT NULL DEFAULT 0 CHECK(complete IN (0,1)),
  PRIMARY KEY(path_key,node_hash,node_length)
) WITHOUT ROWID;
CREATE INDEX ix_directory_parent_root_work ON directory_parent_roots(path_key,seen_generation,complete,node_hash,node_length);
CREATE TRIGGER parent_proof_child_insert AFTER INSERT ON local_files
BEGIN UPDATE directory_parent_proofs SET child_epoch=child_epoch+1 WHERE path_key=NEW.parent_key; END;
CREATE TRIGGER parent_proof_child_update AFTER UPDATE ON local_files
BEGIN UPDATE directory_parent_proofs SET child_epoch=child_epoch+1 WHERE path_key IN (OLD.parent_key,NEW.parent_key); END;
CREATE TRIGGER parent_proof_child_delete AFTER DELETE ON local_files
BEGIN UPDATE directory_parent_proofs SET child_epoch=child_epoch+1 WHERE path_key=OLD.parent_key; END;
CREATE TRIGGER parent_proof_remote_insert AFTER INSERT ON remote_files
BEGIN UPDATE directory_parent_proofs SET remote_revision=remote_revision+1 WHERE path_key=NEW.path_key; END;
CREATE TRIGGER parent_proof_remote_update AFTER UPDATE ON remote_files
WHEN OLD.record_body<>NEW.record_body OR OLD.signature<>NEW.signature OR OLD.path_key<>NEW.path_key OR OLD.device_id<>NEW.device_id
BEGIN UPDATE directory_parent_proofs SET remote_revision=remote_revision+1 WHERE path_key IN (OLD.path_key,NEW.path_key); END;
CREATE TRIGGER parent_proof_remote_delete AFTER DELETE ON remote_files
BEGIN UPDATE directory_parent_proofs SET remote_revision=remote_revision+1 WHERE path_key=OLD.path_key; END;

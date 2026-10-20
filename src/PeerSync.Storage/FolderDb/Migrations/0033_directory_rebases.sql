CREATE TABLE directory_rebases (
  path_key TEXT PRIMARY KEY REFERENCES directory_publications(path_key) ON DELETE CASCADE,
  graph_id BLOB NOT NULL CHECK(length(graph_id)=32),
  input_hash BLOB NOT NULL CHECK(length(input_hash)=32),
  input_epoch INTEGER NOT NULL CHECK(input_epoch>=0),
  cursor TEXT,
  complete INTEGER NOT NULL CHECK(complete IN (0,1))
) WITHOUT ROWID;
CREATE TRIGGER directory_rebase_restage AFTER UPDATE OF version ON directory_publications WHEN OLD.version<>NEW.version
BEGIN DELETE FROM directory_rebases WHERE path_key=OLD.path_key; END;

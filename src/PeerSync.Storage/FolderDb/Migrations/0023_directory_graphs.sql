CREATE TABLE directory_graphs (
  id BLOB PRIMARY KEY CHECK(length(id)=32),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  directory_path TEXT NOT NULL,
  node_hash BLOB NOT NULL CHECK(length(node_hash)=32),
  node_length INTEGER NOT NULL CHECK(node_length BETWEEN 12 AND 4620),
  complete INTEGER NOT NULL DEFAULT 0 CHECK(complete IN (0,1))
) WITHOUT ROWID;
CREATE TABLE directory_graph_work (
  id INTEGER PRIMARY KEY,
  graph_id BLOB NOT NULL REFERENCES directory_graphs(id) ON DELETE CASCADE,
  parent_path TEXT NOT NULL,
  parent_key TEXT NOT NULL,
  node_hash BLOB NOT NULL CHECK(length(node_hash)=32),
  node_length INTEGER NOT NULL CHECK(node_length BETWEEN 12 AND 1048588),
  kind INTEGER NOT NULL CHECK(kind IN (0,1)),
  page_depth INTEGER NOT NULL CHECK(page_depth BETWEEN 0 AND 9),
  complete INTEGER NOT NULL DEFAULT 0 CHECK(complete IN (0,1)),
  UNIQUE(graph_id,parent_key,kind,node_hash)
);
CREATE INDEX ix_directory_graph_pending ON directory_graph_work(graph_id,complete,id);
CREATE TABLE directory_graph_members (
  graph_id BLOB NOT NULL REFERENCES directory_graphs(id) ON DELETE CASCADE,
  path_key TEXT NOT NULL,
  record_hash BLOB NOT NULL CHECK(length(record_hash)=32),
  record_length INTEGER NOT NULL CHECK(record_length BETWEEN 48 AND 624),
  PRIMARY KEY(graph_id,path_key)
) WITHOUT ROWID;
CREATE TABLE directory_graph_references (
  graph_id BLOB NOT NULL REFERENCES directory_graphs(id) ON DELETE CASCADE,
  hash BLOB NOT NULL CHECK(length(hash)=32),
  length INTEGER NOT NULL CHECK(length BETWEEN 1 AND 8388608),
  PRIMARY KEY(graph_id,hash)
) WITHOUT ROWID;

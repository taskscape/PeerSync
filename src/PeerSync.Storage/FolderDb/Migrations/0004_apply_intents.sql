CREATE TABLE apply_intents (
  id TEXT PRIMARY KEY CHECK(length(id)=32),
  path_key TEXT NOT NULL UNIQUE CHECK(length(path_key) BETWEEN 1 AND 4096),
  expected_local_seq INTEGER CHECK(expected_local_seq IS NULL OR expected_local_seq>=0),
  payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 25165824)
);

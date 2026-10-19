CREATE TABLE archive_policy_releases (
  source_id TEXT PRIMARY KEY REFERENCES archive_provenance(source_id),
  root_id BLOB NOT NULL CHECK(length(root_id)=24),
  released_ticks INTEGER NOT NULL CHECK(released_ticks>0),
  reason INTEGER NOT NULL CHECK(reason IN (1,2)),
  witness_id TEXT REFERENCES archive_retirements(id)
) WITHOUT ROWID;

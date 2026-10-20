CREATE TABLE case_block_routes (
  source_id BLOB PRIMARY KEY REFERENCES case_preservations(source_id) ON DELETE CASCADE,
  route_hint BLOB CHECK(route_hint IS NULL OR length(route_hint)=32),
  route_after BLOB CHECK(route_after IS NULL OR length(route_after)=32)
) WITHOUT ROWID;
CREATE TRIGGER case_block_routes_completed AFTER UPDATE OF completed ON case_preservations WHEN NEW.completed=1
BEGIN DELETE FROM case_block_routes WHERE source_id=NEW.source_id; END;

ALTER TABLE local_files ADD COLUMN parent_key TEXT NOT NULL DEFAULT '';
ALTER TABLE local_files ADD COLUMN path_sort_key BLOB;
UPDATE local_files SET parent_key=rtrim(rtrim(path_key,replace(path_key,'/','')),'/');
CREATE INDEX ix_local_directory_children ON local_files(parent_key,path_sort_key);
CREATE INDEX ix_local_directory_sort_backfill ON local_files(path_key) WHERE path_sort_key IS NULL;

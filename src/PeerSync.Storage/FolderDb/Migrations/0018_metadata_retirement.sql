ALTER TABLE archive_contents ADD COLUMN retiring INTEGER NOT NULL DEFAULT 0 CHECK(retiring IN (0,1));
DROP INDEX ix_archive_contents_active;
CREATE UNIQUE INDEX ix_archive_contents_active ON archive_contents(source_id) WHERE active=1 AND retiring=0;
CREATE INDEX ix_archive_contents_retiring ON archive_contents(retiring,source_id,id);

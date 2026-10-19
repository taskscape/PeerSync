ALTER TABLE local_files ADD COLUMN content_ready INTEGER NOT NULL DEFAULT 0 CHECK(content_ready IN (0,1));

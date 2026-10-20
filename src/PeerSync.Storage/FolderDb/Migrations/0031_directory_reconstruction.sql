ALTER TABLE directory_publications ADD COLUMN reconstruction_only INTEGER NOT NULL DEFAULT 0 CHECK(reconstruction_only IN (0,1));

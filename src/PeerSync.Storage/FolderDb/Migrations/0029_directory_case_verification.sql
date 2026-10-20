ALTER TABLE directory_case_progress ADD COLUMN verify_epoch INTEGER CHECK(verify_epoch IS NULL OR verify_epoch>=0);
ALTER TABLE directory_case_progress ADD COLUMN verify_root_seq INTEGER CHECK(verify_root_seq IS NULL OR verify_root_seq>=0);
ALTER TABLE directory_case_progress ADD COLUMN verify_rw INTEGER CHECK(verify_rw IS NULL OR verify_rw IN (0,1));
ALTER TABLE directory_case_progress ADD COLUMN verify_key TEXT;
ALTER TABLE directory_case_progress ADD COLUMN verify_complete INTEGER NOT NULL DEFAULT 0 CHECK(verify_complete IN (0,1));
ALTER TABLE case_preservations ADD COLUMN retained_until_ticks INTEGER CHECK(retained_until_ticks IS NULL OR retained_until_ticks BETWEEN 1 AND 3155378975999999999);
CREATE INDEX ix_case_retained_directories ON case_preservations(root_id,completed,retained_until_ticks,source_id);

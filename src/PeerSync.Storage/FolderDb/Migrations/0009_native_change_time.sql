ALTER TABLE local_files ADD COLUMN change_filetime INTEGER CHECK(change_filetime IS NULL OR change_filetime>=0);

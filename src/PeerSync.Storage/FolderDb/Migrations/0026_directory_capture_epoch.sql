ALTER TABLE directory_captures ADD COLUMN input_epoch INTEGER CHECK(input_epoch IS NULL OR (typeof(input_epoch)='integer' AND input_epoch>=0));
CREATE TABLE directory_capture_clock (
  id INTEGER PRIMARY KEY CHECK(id=1),
  epoch INTEGER NOT NULL CHECK(typeof(epoch)='integer' AND epoch>=0)
);
INSERT INTO directory_capture_clock(id,epoch) VALUES(1,0);
CREATE TRIGGER directory_capture_insert AFTER INSERT ON local_files BEGIN UPDATE directory_capture_clock SET epoch=epoch+1 WHERE id=1; END;
CREATE TRIGGER directory_capture_update AFTER UPDATE ON local_files BEGIN UPDATE directory_capture_clock SET epoch=epoch+1 WHERE id=1; END;
CREATE TRIGGER directory_capture_delete AFTER DELETE ON local_files BEGIN UPDATE directory_capture_clock SET epoch=epoch+1 WHERE id=1; END;

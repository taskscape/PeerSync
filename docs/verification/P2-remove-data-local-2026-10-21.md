# P2 `--remove-data` local verification — 2026-10-21

`PeerSync.Service.exe --remove-data` now requires an elevated Windows process, stops the `PeerSync` service or waits for it to reach Stopped within 30 seconds, reads the current settings or a valid backup for the certificate thumbprint, validates that the deletion root is exactly `%ProgramData%\PeerSync`, rejects reparse points anywhere in the tree, removes matching PeerSync device certificates and persisted CNG keys, and deletes the service data directory. The completion message states that synced folders were not changed.

The focused `PeerSync.Service.Tests` run passed 22 tests with zero failures or skips. New removal tests verify command ordering, the 30-second timeout, argument/elevation guards, and actual deletion of a temporary service-data tree while a sibling synced-folder fixture remains intact. The latest canonical local run passed all 1,663 tests and 14 simulation cases, probes, publishing, and 600-second fuzz with zero crashes.

Tests use a fake service/certificate platform for the command orchestration. They do not stop the installed Windows service or remove anything from the host's `LocalMachine\My` certificate store. Those native checks remain open before P2.5.3 is complete.

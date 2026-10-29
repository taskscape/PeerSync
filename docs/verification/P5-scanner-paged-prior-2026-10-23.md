# P5.3 bounded prior-index lookup, 2026-10-23

The scanner previously held every signed prior `LocalFileRow` in a dictionary while also retaining the native walk and pending updates. It now streams prior rows for rename/deletion decisions and fetches only the current 1,000-entry page during hashing and history retention. The initial empty-index case skips prior-row lookups. Each bulk query binds canonical path keys as parameters.

This reduces the live prior-row set to one page during unchanged rescans; it does not bound the native walk or pending update collection. The measured 2.55 GB peak remains an open P5.3 result until a fresh committed-source million-file run demonstrates otherwise. The remaining on-disk observation/update staging and recovery invariants are specified in [the bounded staging design](../proposals/P5-bounded-scanner-staging.md).

Validation on this change: Storage 302/302 and Engine 1,038/1,038 passed with zero skips. A new 1,001-file native scanner case crosses the lookup page boundary, then verifies an unchanged rescan, one edit, one deletion and the retained row count; it passed 1/1. Logs are retained under `artifacts/tests/p5-storage-paged-prior-20261023.log`, `artifacts/tests/p5-engine-paged-prior-20261023.log`, and `artifacts/tests/p5-engine-paged-prior-boundary-20261023.log`. The full Engine run preceded addition of that boundary case; the final source was rebuilt with zero warnings and the new case passed separately.

The existing committed-source million-file fresh-peer run uses its own earlier binary/database and was not restarted or modified by this change.

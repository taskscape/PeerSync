# P5.3 disk-backed native observation, 2026-10-23

The scanner's native walk now writes canonical paths and native metadata to a
private SQLite observation database next to its folder index. The database has
a random scan ID and root-identity binding. A separate exclusive lock marks an
active owner. A later scan discards only abandoned observations for that index;
it leaves a live owner's files alone. The observation is never published as an
index or used after a crash as deletion evidence: a new scan walks the native
tree again. Cleanup failure leaves the lock for another attempt and emits a
sanitized warning.

Hashing reads at most 1,000 observations at a time. Directory publication
reads direct children in UTF-16 ordinal order from an indexed query, retaining
the exact signed fingerprint order for supplementary Unicode characters.
Names that cannot be encoded losslessly for SQLite stay in a small unsyncable
fallback set and retain the scanner's protected-path behavior. Existing
root-identity rechecks, history retention, durable block capture, and
conservative deletion decisions remain in force.

The final full Engine run with this implementation passed 1,041/1,041 cases
with zero skips, including the UTF-16 ordering case and the 1,001-file prior-row
page boundary. The abandoned-observation case also passed: an exclusive
owner kept its stage, then the next scan discarded it after the lock was
released while preserving user bytes. Its full-suite result is recorded in
`artifacts/tests/p5-engine-observation-final-20261023.log`.
After adding an explicit check of the stored root identity, the scanner and
Unicode inventory focus passed 20/20 with zero skips; see
`artifacts/tests/p5-engine-observation-root-binding-20261023.log`.
The final focused 20/20 rerun also covers the ignored-file-prefix reduction:
only ignored directories need to shield descendants in memory; ignored files
are rechecked by the matcher during streamed prior-row and rename decisions.
Its log is `artifacts/tests/p5-engine-observation-ignore-final-20261023.log`.
The final observation reader uses its path-key primary index for unordered
hashing pages, avoiding a million-row SQLite sort; UTF-16 ordinal sorting is
still required for direct children. The final-source scanner/Unicode focus
passed 20/20 with zero skips at
`artifacts/tests/p5-engine-observation-final-source-focus-20261023.log`.

A fresh 10,000-file native scanner control over the retained corpus passed:
10,000 files hashed on first index, 10,010 rows, 10,010 unchanged on rescan,
zero rehashed or updated, sequence preserved, and `correct=true`. First index
took 2.209 seconds and the unchanged scan 1.360 seconds while the older
million-file peer and 24-hour fuzz processes were running. Its binary SHA-256
was `67C6958B6410A6775FDC5F5405B76B836B67DB18454FE3A4F492DEFE40E739A3`.
The base revision, working patch, copied new source, hashes, retained new
database, and output are under
`artifacts/scale/p5-observation-staging-10k-20261023/`. No observation
database or lock remained after the control.

This is an intermediate memory reduction. Pending signed updates and the
changed-path result still grow with the first scan, so the 300 MB peak target
and a committed-source million-file measurement remain open. The existing
million-file fresh-peer process uses an earlier separate binary/database and
was not modified.

# Fresh-peer parent scheduling, 2026-10-23

The million-file fresh-peer candidate from commit `79d4e46` remains a separate,
older running binary. Its source published 1,002,000 records and entered peer
convergence. The first convergence batch had 8,000 needed records, visited
1,000 and failed all 1,000 with `Win32Exception` native error 2. Later batches
continued to fail files while receiving additional index pages. A read-only
inspection of its retained peer index found 65,000 stage-1 file work rows and
zero `local_files` rows; the earliest file keys were under `0000/`. The source
publishes its 1,000 directory records after its million file records, so this
candidate attempted children before receiving their parents. The retained
logs and database support this causal diagnosis; they do not constitute a
successful million-file transfer.

Current source filters scheduled live work with a nested path until the direct
parent is installed as a clean, live native directory in `local_files`.
Root-level work and deletion stages remain eligible. The next-wake query uses
the same filter, avoiding a busy retry cycle while index pages are still
arriving. Once a directory is installed, the puller refreshes live work within
its existing 1,000-item pass budget and processes deletions after live work.
This preserves normal one-pass nested application without manufacturing a
missing-parent failure and persisted retry for each early file.

The Storage regression covers 1,000 early children ahead of root-level work,
missing/uncertain/deleted parents, later parent installation and the next-wake
result. The Engine regression delivers a signed child index page before its
parent page and checks that no native failure or retry is recorded, then checks
the resulting native bytes when the directory arrives. The existing nested
application/priority test also guards stage ordering across refreshed batches.
The local Storage suite passed 305/305, the Engine suite passed 1,043/1,043,
and the Puller class passed 28/28 after the final empty-batch optimization.
The Release solution build passed with zero warnings and errors.

The active `79d4e46` process is intentionally unchanged by this source edit.
A fresh committed-source million-file run, with its own binary provenance and
exact native-byte verification, remains required for P5.3. Physical two-PC
validation and the other open phase gates remain separate.

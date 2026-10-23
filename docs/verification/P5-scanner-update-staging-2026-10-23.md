# P5.3 bounded scanner update staging, 2026-10-23

The native observation database now also stages pending signed `LocalFileRow`
updates in 1,000-row pages. It preserves vectors, signatures, optional indexed
chunks, native identity and change-time metadata. Prior history decisions read
those staged rows in path order; retained conflict copies are looked up by
file ID and whole-file hash. The index receives all staged local updates in a
single SQLite transaction, so cancellation or process interruption cannot
publish a prefix of the scan. Existing root-identity checks, guarded hashing,
durable block capture and history retention remain before commit.

For more than 10,000 changed rows, `ScanResult` requests a full needed-work
rebuild and does not retain a changed-path array. The rebuild merges ordered
local, remote and needed index cursors, deduplicating without SQLite's
in-memory temporary `UNION` table. Smaller incremental requests still freeze
caller-owned paths before waiting for the reference gate.

Local validation:

| Check | Result |
| --- | --- |
| Storage suite including interrupted 1,001-row commit and three-index path merge | 304 passed, 0 failed/skipped; `artifacts/tests/PeerSync.Storage.Tests_net10.0-windows10.0.20348.0_x64.trx` |
| Engine suite after restoring queued-request input ownership | 1,041 passed, 0 failed/skipped; `artifacts/tests/PeerSync.Engine.Tests_net10.0-windows10.0.20348.0_x64.trx` (subsequently overwritten by focused run) |
| Final scanner focus after the staged-chunk length guard | 33 passed, 0 failed/skipped; same TRX path |
| Native 20,000-file first and unchanged scans over a new index | 20,000 hashed, 20,020 rows, 20,020 unchanged, 0 rehashed/updated, sequence preserved, `changedPathsBounded=true`, `correct=true`; first index 5.099 seconds, unchanged 2.734 seconds |

The 20,000-file corpus and both resulting indexes are retained at
`artifacts/scale/p5-staged-updates-20k-20261023/`. The scanner control ran
while the older, separate million-file fresh-peer process and 24-hour fuzz
campaign were active. The older process does not use this scanner source.

This evidence does not complete P5.3: a committed-source million-file run must
sample peak working set and private bytes under 300,000,000 bytes throughout
the first and unchanged walks, meet the 20-minute first-index target, and
verify fresh-peer delivery. A million-row deletion could still retain rename
candidates; a mass native cache-stamp refresh can retain cache updates. Those
paths need measurement or further bounding. Full Simulation has not yet been
rerun on this exact source, and hosted GitHub Actions remain blocked by budget.

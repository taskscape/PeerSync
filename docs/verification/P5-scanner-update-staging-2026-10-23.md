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

A follow-up source change streams prior path names before loading signed rows:
an unchanged million-row index no longer deserializes every prior envelope
twice. Missing-path rename candidates and native change-time cache updates
are staged in bounded pages in the same disposable workspace. The current
million-file run below was launched from the preceding `ab14f0b` commit and
does not contain this follow-up; its results must be attributed separately.

Local validation:

| Check | Result |
| --- | --- |
| Storage suite including interrupted 1,001-row commit and three-index path merge | 304 passed, 0 failed/skipped; `artifacts/tests/PeerSync.Storage.Tests_net10.0-windows10.0.20348.0_x64.trx` |
| Engine suite after restoring queued-request input ownership | 1,041 passed, 0 failed/skipped; `artifacts/tests/PeerSync.Engine.Tests_net10.0-windows10.0.20348.0_x64.trx` (subsequently overwritten by focused run) |
| Final scanner focus after the staged-chunk length guard | 33 passed, 0 failed/skipped; same TRX path |
| Native 20,000-file first and unchanged scans over a new index | 20,000 hashed, 20,020 rows, 20,020 unchanged, 0 rehashed/updated, sequence preserved, `changedPathsBounded=true`, `correct=true`; first index 5.099 seconds, unchanged 2.734 seconds |
| Follow-up source Engine suite after path-only prior reads and disk-staged rename/cache rows | 1,041 passed, 0 failed/skipped; `artifacts/tests/PeerSync.Engine.Tests_net10.0-windows10.0.20348.0_x64.trx` |
| Follow-up source 20,000-file first/unchanged native control | `correct=true`, `changedPathsBounded=true`, first index 5.672 seconds, unchanged 2.593 seconds; `index-140121a2519b4095b3222bc9852eac89.db` in the same retained corpus |

The 20,000-file corpus and both resulting indexes are retained at
`artifacts/scale/p5-staged-updates-20k-20261023/`. The scanner control ran
while the older, separate million-file fresh-peer process and 24-hour fuzz
campaign were active. The older process does not use this scanner source.

This evidence does not complete P5.3: a committed-source million-file run must
sample peak working set and private bytes under 300,000,000 bytes throughout
the first and unchanged walks, meet the 20-minute first-index target, and
verify fresh-peer delivery. An adversarial tree with a million unreadable or
colliding paths could still retain reported problems or protected prefixes;
the boundedness of those paths needs review. The scale runner now samples
working set and private bytes every 250 ms through both scans and applies the
exact 300,000,000-byte ceiling to both sampled peaks and the OS working-set
peak; the preceding `ab14f0b` binary did not yet have this sampler. Full
Simulation has not yet been
rerun on this exact source, and hosted GitHub Actions remain blocked by budget.

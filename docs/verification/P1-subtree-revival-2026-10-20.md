# Retained subtree-deletion revival: local repair checkpoint

[PR #41 run 39397977242](https://github.com/taskscape/PeerSync/actions/runs/39397977242)
fails generated hierarchy seed 794669444. Its other eleven Simulation methods
pass, including all five retained regressions. The other four CI jobs pass;
downloaded unit TRX proves 1550 results and nineteen exact directory controls.
Fuzz completes 112247 seeds in 600.006123 seconds with zero crashes. That does
not qualify the failed run or permit merging/campaign activation.

The exact seed reproduces locally on unchanged submitted production sources:
exit 2, 60.891 test-process seconds. Six peers perform fourteen operations;
all 24 healing rounds retain different visible trees and three peers fail two
live child items. The retained journals identify Win32 error 2 in directory
application. Some peers physically retain a nonempty directory after logically
recording its signed tombstone; the other peers remove the empty parent and
cannot open paths for later live children.

Read-only inspection preserves the hosted fixture. Copied decision databases
must include their SQLite WAL; an initial database-only copy showed stale
state and is excluded from causal evidence. The corrected complete copies
match the original read-only state. The signed parent remains tombstoned while
surviving peers repeatedly stage its revival with an older incidental native
mtime, allowing concurrent replicas to keep selecting the old deletion.

RW scanning now treats only an observed signed-tombstone directory revival as
a new local event: its published directory mtime exceeds the verified deletion
time and is at least the observed native time/current clock. Files and ordinary
live directories keep their existing mtime policy. Snapshot staging retains
the first template timestamp across repeated scans. An unrepresentable revival
time raises an explicit error while preserving the signed tombstone and native
children. No parent is blindly created and no ownership/reference/apply guard
is bypassed.

Both legacy/snapshot timestamp controls fail before repair, then pass. The exact
six-node seed then passes in eight rounds, 34.948776 scenario seconds, exit 0 in
36.494 test-process seconds: thirteen writes, three renames, five deletions,
two directory renames/two subtree deletions, all thirteen published versions
preserved, no staging files and all native/reference/queue checks complete.
It is the sixth permanent hierarchy regression; normal CI and campaign leader
scope include all six. All 312 selected Directory tests pass, including stable
template, timestamp-limit/data-preservation, RW/RO, identity/reparse, recovery
and causal graph controls. All 28 campaign controls pass. Engine/Engine.Tests
Roslyn checks report no warnings/errors or workspace-load warnings.

Evidence: `artifacts/ci41-failed-evidence/`, `artifacts/ci41-failed.log`,
`artifacts/hierarchy-794669444-before*`, `artifacts/hierarchy-794669444-after*`,
`artifacts/hierarchy-794669444-decisions-wal.log`,
`artifacts/revival-clock-before.log`, `artifacts/revival-clock-after.log`,
`artifacts/revival-directory-controls.log` and actual corresponding TRX.
Full canonical, larger generated hierarchy coverage and updated PR/main CI
remain pending. Both live corpus runs retain their immutable earlier sources;
their applicability to this further repair remains unverified. No P1 gate or
task is ticked from these local focused results.

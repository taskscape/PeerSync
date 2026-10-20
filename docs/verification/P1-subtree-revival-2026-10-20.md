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
Full canonical `build/Verify.ps1 -Runtime win-x64` passes on committed repair
9c631e46bf569dbc808164ac892a167d057dfdf5, exit 0 in 613.9128954 seconds.
Actual fresh TRX proves 1554 unit results (1003 Engine plus 551 others), and
the strict scope validator confirms thirteen successful Simulation methods,
all six retained regressions and connected maintenance. Generated smoke
completes eight flat/four hierarchy cases. Maintenance retires two archives,
evicts two blocks and preserves both payload versions. Native probes and
publishing pass. Fuzz completes the four required targets for 600.0015363
seconds, 217565 seeds and zero crashes; its supervisor duration remains separate.
Evidence: `artifacts/p1-revival-canonical.log`, matching scope/unit JSON and saved
canonical TRX, plus
`artifacts/simulation/20261020T100350-32fe9420e97a42e199a935574291e45f/`.

A larger 100-case hierarchy batch also passes on those unchanged binaries,
exit 0 in 43m17.719s test-process time (2596.2877171 scenario seconds). The
terminal summary, single successful method in TRX, 100 distinct replay seeds
and unchanged Engine/Simulation/Fuzz hashes are independently checked in
`artifacts/hierarchy-revival-expanded-100-audit.json`. All three-to-seven node
counts occur. The cases perform 1439 operations, 1285 writes, 274 renames,
481 deletions, 117 directory renames, 188 subtree deletions and 84 kind changes;
all 1285 published payload versions survive. Maximum healing is 23 of the
unchanged 24 rounds. Native equality, stable index high waters, covered
references and needed/recovery/case/publication queue drain remain required.

Four cases each report one root staging file: seeds 365615982, 187918476,
853246416 and 45124414. This diagnostic is not a zero-staging acceptance
requirement: the existing protected staging lifecycle retains owned objects
for at least 24 hours, and regression 1146310622 explicitly permits parked
internal staging at user-tree convergence. No production oracle or policy was
changed. The first audit script incorrectly imposed zero staging; it was
corrected to retain the actual counts and apply the existing acceptance scope.
Evidence: `artifacts/hierarchy-revival-expanded-100/`, its separate TRX and log.

Updated PR/main CI remains pending. Both live corpus runs retain their
immutable earlier sources; their applicability to this further repair remains
unverified. No P1 gate or task is ticked from these local results.

The updated 4c574b9 PR run 39411518907 subsequently passes all thirteen actual
Simulation methods, all six retained regressions, eight flat/four hierarchy
samples and connected maintenance. This exact seed preserves thirteen versions
in eight rounds on the hosted runner. Actual fuzz reaches 600.0036179 seconds,
108834 seeds and zero crashes. X64/ARM64/Server 2022 also pass, with 1554 unit
results independently verified. Server 2025 times out after 934 of its 1002
required Engine results, so the overall run remains failed and merge/phase
acceptance stays open. The separate [native critical-path repair](P1-native-server-critical-path-2026-10-20.md)
retains its failure evidence and does not change these product sources or
production convergence oracles.

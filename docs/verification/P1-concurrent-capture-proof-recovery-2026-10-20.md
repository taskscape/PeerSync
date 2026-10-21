# Concurrent capture proofs: focused recovery repair

The large bf68d97 corpus has drained its visible work queues but cannot qualify
reference coverage. Read-only SQLite and native inspection finds all 782
directory graphs complete per node, no queued captures, and one valid capture
proof on node 1. The proof's checksum/root/marker identity verifies against the
immutable original Engine binary. Its independent 48-byte final block matches
the expected hash/length and committed catalog native ID, while its former
temporary is absent and the proof's original data ID differs. The proof was
captured at 2026-10-20 09:08:22 UTC. This is not a completed native corpus or a
zero-lost-version result.

Two focused regressions reproduce on unchanged submitted production code:
the independent concurrent loser leaves two proofs after its canonical catalog
flush where one is expected, and obsolete proof recovery keeps
`AllKnownSourcesCovered=false`. Actual test exit is 2; both cases fail in
2.181 test-process seconds. Evidence: `artifacts/concurrent-capture-before.log`
and its actual TRX. Design/decision/WIP commit 7588ab2 precedes implementation.

The installation loser now retains its own proof in the pending proof set
until FULL registration of the winning native object. Existing flush recovery
then retires only that verified proof metadata. Cold recovery also reconciles
a verified obsolete proof when its old temporary is confirmed absent and a
guarded independent final object has exact hash/length plus an existing
committed root/hash/length/native-ID catalog match. An indexed read-only
ownership predicate avoids catalog scans and never registers a replacement.
No data object is deleted or adopted. Foreign temporaries, missing/foreign
catalog entries, changed bytes, hardlinks, reparse paths and invalid proofs
remain protected. Actual staged-byte retention keeps its 24-hour policy.

Ten Engine controls pass in 2.195 test-process seconds: both counterexamples,
seven catalog/native negative cases and failed-registration owner restart.
Thirteen Storage catalog controls pass in 2.726 seconds, including exact tuple
matching and no implicit registration. The recovery diagnostic contains a
constant reason/count/session with no root path, block hash, file ID or payload.
Scale reference refreshes now receive their existing node journal, making
coverage/recovery reasons visible without changing convergence checks.
Engine/Storage, both test projects and Benchmarks Roslyn checks have no
warnings/errors or workspace-load warnings.

Full `build/Verify.ps1 -Runtime win-x64` subsequently passes on committed repair
dd7059b43b25ff18614d7145211e8851c668a23a, exit 0, manifest duration 629.6409798
seconds. Fresh actual TRX proves 1565 unit results across nine projects
(1013 Engine/300 Storage); the strict Simulation scope confirms thirteen
successful methods, all six retained regressions and connected maintenance.
Generated smoke passes eight flat/four hierarchy cases. Maintenance retires two
archives and evicts two blocks while both versions survive. Native probes and
publishing pass. Fuzz completes all four targets, 210690 seeds in 600.0030007
actual seconds with zero crashes. `artifacts/p1-capture-recovery-release/`
snapshots the manifest, actual scope/unit/TRX/raw fuzz evidence and five source-
bound binary hashes/informational versions. Product/build inputs still match
the committed repair. Separate console evidence is
`artifacts/p1-capture-recovery-canonical.log`.

Only after the old process is terminal and its source/binaries/fixture are
preserved, its clean managed worktree is reused at the committed repair. A
locked build followed by unchanged-binary `-NoBuild` checks passes 32 files in
15.4699812 seconds/six rounds and 4096 files in 753.1064081 seconds/29 rounds.
The latter has 4096 initial/4092 final native files after 24 overwrites/four
renames/four deletions; all 4120 versions survive, references cover and all
queues drain. Actual clean source/exit 0, binary hashes and successful fixture
removal are independently checked, including native absence of both fixtures.
Evidence is copied to `artifacts/scale-capture-{32,4096}-dd7059b/`.
The same immutable binaries now run a fresh 100000-file corpus with a twelve-
hour deadline. This full outcome and updated hosted PR/main acceptance remain
pending; no P1 gate or task is ticked from local/preflight results.

The old run is preserved as a confirmed nonconvergent diagnostic case. Before
canceling, the exact executable path, source arguments, PID 13812 and creation
time were verified using both a bound process handle and CIM. Only that owned
benchmark process was terminated at 12:47:21 UTC; exit -1 is recorded. Its parent
session exits 1 and writes clean bf68d97 source state with runner exit -1;
no scenario summary exists. The original complete fixture remains in place,
and post-cancel validation with saved original binaries confirms the same valid
proof/canonical state. Neither its proof nor data was manually repaired.
Other corpus processes were not canceled.

`artifacts/scale-bf68-obsolete-proof-evidence/` retains immutable binary
hashes/informational versions, process/source/cancellation metadata, progress
before/after cancellation, original proof/generated block bytes and catalog
observations. Byte copies explicitly do not preserve native identities; the
original retained fixture and read-only native validation are the identity
evidence. The repaired-source full corpus must run separately. Other live
earlier-source runs remain bound to their real revisions and are not relabeled.

Before this further repair, PR #41 head 95464be through synthetic merge aac4159
passes all five jobs in run 39416346838: 1554 distinct unit results, 1002
additional Server 2025 results with only the existing capacity exclusion,
thirteen Simulation methods/six retained regressions/one maintenance scenario,
and 106213 fuzz seeds in 600.0042291 seconds with zero crashes. Its fuzz
supervisor takes 2532.8416656 seconds, separate from actual fuzz duration.
The exact artifacts and strict scope are under `artifacts/ci41-budget-*`.
That green bounded run does not cover the new two-owner state, so PR #41 is
held unmerged until this additional repair meets local and PR/main acceptance.

Subsequently, [final PR/main acceptance](P1-convergence-allocation-ci-merge-2026-10-20.md)
passes all five jobs on the repaired source. The older immutable 4c574b9 full
corpus independently [confirms the same stale-proof state](P1-scale-revival-capture-deferral-2026-10-20.md)
with a verified 246-byte canonical block. Its identified process is stopped
after read-only native/catalog validation and evidence preservation; its
original fixture remains unmodified. It is not a passing full corpus.

The fresh immutable repaired-source full run subsequently
[completes successfully](P1-scale-capture-100000-2026-10-20.md), with parent
exit 0, clean dd7059b source and unchanged preflight binaries. It preserves all
100024 versions after the actual 100000-file initial corpus and partitioned
mutations; all five native/reference/queue checks and guarded cleanup pass.
The fixture is independently absent. Combined with final PR/main acceptance,
this closes P1.2.4b1a and its reopened ownership/retention parents. Only the
corpus line of Gate P1 closes; the five-date campaign remains open.

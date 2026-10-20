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
Engine/Storage and both test-project Roslyn checks have no warnings/errors or
workspace-load warnings. Full repaired-source release/corpus and hosted
acceptance remain pending; no task is ticked from these focused checks.

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

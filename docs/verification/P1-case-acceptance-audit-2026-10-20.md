# Remaining case-preservation acceptance audit

This audit uses merged main `7b689d0f583835424f632983203e58049704dea9`,
its exact tested tree `0271d70475cb917498797cbfcea179772b507ed7` and successful
main CI run 39374744845. It does not infer completion of the corpus, five-day,
production transport or installed-service requirements.

## P1.5.3h4a: directory baseline and signed wire foundations

- Retained original native baseline: `artifacts/p1-directory-case-baseline.log`
  and its TRX record seven cases, five failures/two controls passing, exit 2.
  Both arrival orders, flat/nested children and file-over-directory failure
  triggers remain in the current connected suite.
- `tests/PeerSync.Protocol.Tests/DirectorySnapshotTests.cs` verifies canonical
  empty-root bytes versus legacy absence, sorted/frozen buffers, 128-link bounds,
  maximum blobs/envelopes, every short-node truncation/trailing bytes, full
  encoded hash/length, original unknown-field/signature order and 5000 arbitrary
  inputs. Invalid directory-root pairs are rejected after authentication and
  tampering cannot retain the old signature.
- `SignedRecordFactory.cs` validates signed hash/length pairing, live-directory
  type and canonical encoded-length bounds. The added codec tests are present
  in the actual main Protocol TRX: all 24 Protocol results pass, including all
  17 directory snapshot results. Current connected baseline counterparts pass
  in the actual main Engine TRX (994/994); this foundation itself is not an
  authorization to mutate directory copies.
- Local foundational build/focused proof is retained in
  `artifacts/p1-directory-codec-tests.log`; final full/CI/main proof is in
  `artifacts/ci38-main-x64/` and `artifacts/ci38-main-server2022/`, with the
  exact-tree merge audit linked from plan.md.

Conclusion: the scoped foundation meets its Done-when criteria and may be ticked
with #38. Other unchecked case/graph/recovery items still require their own
requirement/evidence audit; group test counts alone do not close them.

## Requirement-to-main test linkage

The follow-up audit reads each attributed case/directory test declaration and
requires its exact method name to occur with a Passed outcome in downloaded
main TRX. This covers 39 source files, 306 test method declarations and 439
parameterized results; zero declarations are missing. Helper methods are
excluded. The linkage is retained in
`artifacts/ci38-main-case-method-coverage.json`, produced by
`artifacts/audit-case-test-coverage.py`. This confirms presence and outcome;
the requirements below also depend on source and assertion inspection.

## P1.5.3h1/h2: baseline, approved policy and durable admission

The original regular-file baseline records four failing empty/existing-receiver
cases and two passing causal rename controls. The approved naming proposal and
source design precede mutation implementation, as documented in the h1/h2
checkpoints. `CaseConflictPolicy` hashes the original signed body/signature
with the specified domain, selects causally maximal distinct leaf spellings,
and shares the deterministic conflict ordering. Six policy results cover
arrival/forwarder independence, equal payloads, causal renames with edits,
parent-only spelling, dominated sources and bounded UTF-16 names.

Storage's original admission and expanded lifecycle cases all pass on main.
`SourcesSurviveReopenRowReplacementAndNeededRemovalWithExactOriginalBytes`,
`ForwardedDuplicateIsIdempotentButMismatchedIdentityRollsBackAllReceiptState`,
`FailedFinalAdmissionRollsBackNewSourcesAndExistingCursor` and
`CapacityBackpressureRollsBackSourcesNeededRowsAndCursorTogether` verify frozen
source/root obligations, replay and whole-transaction rollback at capacity.
The 299-result Storage suite includes migration and reference/expiry guards;
the six connected regular-file baseline/control counterparts now pass.
The h1 and h2 child criteria are complete; admission alone is not the parent.

## P1.5.3h3/h3a/h3b: guarded files, gossip, backpressure and lifecycle

The 27 native `CaseCopyRecoveryTests` results cover every retained restart
boundary, RW/RO scan-before-release, unknown file/directory and reparse
occupants, same-byte collision, prepared edits, signed attribute restoration,
row replacement, concurrent source receipt during download, cancellation,
independent-work fairness and actual hosted case-only timer wakeup. Kind-7
recovery keeps source/root/destination/GUID/FID authority through no-overwrite
installation; publication/visible verification completes the source afterward.
An authenticated ready gossip copy may satisfy visibility without acquiring
deletion ownership. Unknown objects continue to allocate another slot.

The retained 1001-path startup and partially occupied capacity tests exercise
the public coordinator, preserve unavailable older sources, and drain valid
prefixes while ordinary original-path application stays blocked. Storage
tests verify failing-path rollback, cancellation/noncapacity batch rollback,
persistent retry deadlines under clock rollback, 24-hour metadata compaction,
portable completed witnesses and pending staging/reference protection.
Connected forwarder disconnection and repeated RO passes both drain with
correct visible bytes. PR #36's historical final-head/review proof is retained;
the later exact-tree #38 PR/main results provide current full acceptance.
These file-specific children meet their criteria, with subtree acceptance below.

## P1.5.3h4b1: capture and graph primitives

Source and assertions retain exact original authenticated envelopes, bounded
canonical builders/checkpoints, root/native identity, frozen buffers, indexed
epoch continuation and FULL publication/draft release. Main includes 24 builder,
16 metadata/block, 29 capture and 17 graph test results. Tests verify interrupted
prefixes/reopen, identical-manifest epoch changes, changed membership/native
identity, SQL rollback before and after row replacement, cancellation after
independent metadata flush, duplicate structure/keys, wrong root kinds and
excessive graph depth. A 1025-chunk interrupted reference prefix resumes safely.

Storage capture/graph tests verify persisted prefixes and exact CAS, epoch
overflow rollback, bounded reference retirement across reopen and touch/root
guards. Incomplete, legacy and unpublished sources retain the prior active
reference snapshot and prevent native sweep; cache retirement removes metadata
only. These primitives now also run through the production integration below.

## P1.5.3h4b2/b3/b4 and h4b: public publication, metadata and rebase

Public `Puller` and `TransportFolder` enable the snapshot path. Tests construct
these ordinary entry points; standalone Scanner remains intentionally legacy
because it owns no publication worker. Scanner/worker/queue assertions prove
private stable templates retain old signed rows, deepest-first child
dependencies, bounded continuation between full scans, restart/no-op release,
GC/namespace barriers, persistent retry/fairness and incomplete-walk pause.
Public activation cases verify legacy upgrade, RO behavior and unchanged
native mtimes/vector/sequence on repeated scans. Queue completion retains native
handles and atomically compares the template, physical/native root, epoch,
graph, parent clock and old sequence before publication/cache/release.

Thirteen metadata-worker and seven Storage metadata-source results verify
protected original envelopes after replacement, capacity rollback, bounded
ordinal route pages across restart, source timeout/fallback, cache ownership
recovery, cancellation and clock-rollback fairness. Network fetch precedes
reference-gate acquisition. Both four-node RW/RW and RW/RO connected gossip
cases assert complete graph reference coverage in addition to native bytes,
stable names and drained queues. Logical changes trigger needed reconciliation.

Sixteen rebase and two reconstruction results verify exact or causally
dominating signed children, bounded exact-prefix restart, stale epoch/graph
and SQL rejection, delayed metadata/children, tombstones and both kind changes.
Missing/concurrent/unverified children stay pending and RO cannot sign.
Connected offline-owner and edited-copy forwarding with both authors offline
pass through public coordinators. Final publication consumes guarded native
and FULL graph/epoch authority. Therefore b1-b4 and their b parent meet their
scoped acceptance; local preview failures in older reports are historical.

## P1.5.3h4c, h4 and h: subtree recovery and complete coordination

Main's 13 projection, 19 root-recovery, 23 member-recovery, four namespace and
eight completion results cover source/member/native binding, bounded cursor
progress, no-overwrite kind-8/9 journals, real cancellation, every native/durable
boundary, intermediate directory replacement, foreign collisions, edited
staging, exact advertisers, locked writers and changed graph identities.
Completion verifies every native member against its installed FID and signed
source, requires RW publication or RO local scan, then atomically releases
the original source. Epoch/SQL failure retains the obligation. RO graph
retention survives generic compaction; bounded obsolete witness retirement
preserves recovery, pending/current roots and native copies.

All 25 connected case results pass, including empty/nested subtrees under both
arrival orders, both file/directory winners, original-spelling causal controls,
late child/tombstone/reactivation, four-node gossip and offline forwarding.
Legacy absence never supplies an inferred tree. Earlier #38 review, retained
route, parent-clock/root-set and tombstone-cycle repairs have their own completed
audit and permanent hierarchical regressions. Together these satisfy h4c,
h4 and the complete h case-preservation coordination requirements.

## Acceptance boundary

The scoped case children and parents above may be ticked with #38 after this
audit. The current source is already merged on green main; this checklist
update introduces no executable change. It does not close all P1 tasks,
100000-file scale, 1000 CI seeds on five consecutive UTC dates, connected
maintenance follow-up CI, production clock/service integration or later phases.

## P1.5.2/P1.5.3/P1.5.3b: conflict and puller parent integration

The extended actual-main linkage in
`artifacts/ci38-main-pull-method-coverage.json` covers 49 files, 444 attributed
method declarations and 633 passing parameterized results, with none missing.
It includes all 20 resolver, 55 FileApplier, 27 Puller and 17 Storage pull-work
results, plus staging/recovery and the case/directory set above.

The design conflict table's engine outcomes have explicit coverage: causal
dominance, equal-content vector merge, different-content live mtime/delete time
and DeviceId ties, causal-maximal permutations, archive limit/keep-both policy,
RO forwarding without signing, unknown-child directory deletion, both kind
transitions and case-only concurrent names. Invalid native wire names become
unsyncable while independent valid pulls continue; malformed received records
cannot partially commit. Actual user-facing conflict notices/settings remain
the later UI tasks and are not claimed by this engine acceptance.

The real-pass coordinator test asserts parents first, priority files then file
and deepest-directory deletions. Locked-target retries persist across restart;
independent work proceeds and new work invalidates stale backoff. Verified
temporary/local chunk reuse precedes network fallback and whole-file hashing.
Startup recovery precedes scanning, including missing-target cancellation,
corrupt/missing temporary reconstruction, concurrent targets and source changes.
Native FileApplier tests inject every retained destructive boundary and reopen
the DB; exact old bytes remain archived and final installation cannot overwrite
a newly occupied target. This is the approved recoverable two-move protocol,
including its brief externally visible missing-target interval.

The prior coordinator/staging merge audits and current green #38 main include
the remaining range guards, retry, stale-temporary recovery/expiry, authenticated
case mapping and nested hierarchy integration. All Puller children are now
complete. Therefore P1.5.2, P1.5.3b and the P1.5.3 Puller parent meet their
scoped engine criteria. Broader retention simulation and phase gates remain
separate requirements.

# Retention acceptance audit

Status: unit/native requirements verified on merged green main #38;
connected maintenance passes PR #39 final CI and is merged. Its main-push
run 39381507911 is still pending; see the separate #39 merge audit.
This report does not authorize ticking the remaining retention parents yet.

## Persistent ownership, references and recovery

Actual main run 39374744845 on `7b689d0f583835424f632983203e58049704dea9`
passes all 994 Engine and 299 Storage results. The exact method-name audit
`artifacts/ci38-main-retention-method-coverage.json` matches 220 attributed
declarations in 19 archive/history/ownership/reference/retention test files
to 349 passing parameterized results, with zero missing methods. Historical
design/implementation checkpoints and #28/#30/#31/#32 merge evidence remain
linked in plan.md; #38's later full PR/main audit supplies current green proof.

SnapshotHistory keeps prior authenticated content through edits/deletes/kind
changes and all retained native/DB interruption boundaries. Root/source/native
identities and FULL intents prevent adoption of unknown objects or duplicated
accounting. New history reuse receives a fresh retention window; age/cap order
uses that logical hold rather than an older physical archive date. Failed or
missing legacy accounting keeps the original bytes and recovery obligation.

Archive provenance/content manifests preserve exact sources across row removal
and ID reuse. Reference snapshots activate only after complete authenticated,
root-bound enumeration; incomplete capture/legacy/graph coverage retains the
previous active snapshot and withholds sweeping. Current/needed/pending/history
holds serialize with reference-changing engine paths. Policy release requires
exact source/marker/witness authority, with atomic reactivation and visible
deferral for missing or unverified provenance. Absence alone is not expiry.

## Verified cleanup and bounds

The inspected public BlockRetention entry point takes operation then reference
gates, bootstraps history/archive policy, refreshes coverage and reconciles
deletion intents before admitting new deletion work. An owned independent
block must match root, native FID, length and whole hash under retained native
guards before a FULL reservation. Completion proves absence under the pinned
parent and removes only its matching catalog/orphan/intent records.

Native/Storage tests cover grace and legacy windows, age and capacity pressure,
actual native deletion, every interruption/reopen boundary, renewed references
before and after deletion, replacement/recapture, cancellation, SQLite failure,
unknown/changed/locked/hardlinked/reparse objects and diagnostic privacy.
The production 1001-object case deletes at most 1000 then continues next pass.
History retention/provenance, policy release, archive manifests and reference
activation have independent transactional rollback/restart assertions.

## Connected production-path requirement awaiting CI/main

PR #39 adds `ConnectedMaintenanceExpiresArchivesAndBlocksWithoutLosingDelayedPeerVersions`
using three public TransportFolder/IPeerTransport peers and public BlockRetention
sweeps. Local focused/canonical/full-shard/five-shard preflight evidence is in
the connected maintenance report. The actual scenario asserts two expired
archive paths disappear, eligible old block paths disappear, current/needed
and unknown cache bytes survive, a partitioned delayed update completes after
healing, and unchanged global native version/convergence oracles pass.
Only injected fixture clocks advance; the host clock does not change.

Once actual #39 PR/main evidence proves that added case on the merged tested
tree, P1.6.2c and P1.2.4b2b2c/b2b2/b2b/b2/b/P1.2.4 can close from the combined
native/unit/connected proof. Until then their boxes remain unchecked. The full
corpus, five consecutive 1000-seed CI dates and complete P1/production-service
acceptance stay separate regardless of this connected scenario's outcome.

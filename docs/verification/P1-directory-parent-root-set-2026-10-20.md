# Durable directory parent root proof: local checkpoint

Production source: `331310d2d6ef21fdd8c9357076e980e4aca57fb8`.
This supersedes the intentionally failing focused baseline in
`P1-directory-parent-clock-checkpoint-2026-10-20.md`; it does not close P1.

Schema 35 retains authenticated representatives of eligible original parent
roots independently of transient publication queues. Bounded discovery admits
at most 32 envelopes/8 MiB per page. Each root must prove its original direct
children are exact signed current versions or causally dominated by signed
current children/tombstones. Different metadata classes, concurrent children,
missing/corrupt/retiring graphs and changed native/input witnesses cannot
release a parent publication. Tree prefixes survive vector-only restaging;
child and remote revisions prevent stale proof reuse. Discovery, graph work,
proof and cleanup share existing bounded work limits.

Retained sources feed metadata recovery and authenticated block references,
and hold graph retirement. Final reference-gated publication validates the
current root set, child witness and clock floor; publication and proof release
commit atomically. Network fetches remain outside the reference gate. Debug
stages distinguish pending proof, completed proof and proven parent clock
construction without recording paths, payload hashes or signed envelopes.

Focused Engine coverage includes owned/received parents, one-unit continuation,
late root receipt, concurrent child receipt/resolution and missing/corrupt or
retiring graph controls. Storage coverage includes restart, vector forwarding,
readiness/tombstone invalidation, 70-source discovery, graph holds, bounded
native rebinding, exact 1000+5 member continuation, cancellation and SQL
rollback. A cleanup regression verifies that 1000 expired empty proofs consume
the complete shared reference-maintenance budget. The alias fixture now
increments its vector when changing signed spelling; equal-vector different
bodies are correctly rejected.

Isolated full suites: Engine 983/983, no skips, 36.928 seconds; Storage
299/299, no skips, 7.653 seconds. Logs:
`artifacts/p1-parent-proof-final-engine.log` and
`artifacts/p1-parent-proof-final-storage.log`. Compiler/configured analyzer
checks at warning/error are clean for Engine, Engine.Tests, Storage and
Storage.Tests; no workspace diagnostic or truncation was returned.

The separate hierarchical worktree was rebased onto this production source.
Retained six-node seed 1044729930 now passes: 16 operations, 16 ledger versions,
14 healing rounds, exact native convergence, drained work and preservation in
visible files/indexed archives. Scenario time 83.231 seconds; MTP exit 0 in
84.350 seconds. Evidence there:
`artifacts/p1-hierarchical-root-set-first.log` and
`artifacts/simulation/hier1044729930-root-set/`.

Both other retained hierarchy seeds also pass on the same production source:
1648781653 (seven nodes, nine operations, 18 ledger versions, ten rounds,
55.358 scenario seconds, MTP exit 0 in 56.559 seconds) and 1878823561 (seven
nodes, 18 operations, 18 ledger versions, 11 rounds, 62.477 scenario seconds,
MTP exit 0 in 63.573 seconds). Both report zero staging files and pass the
same native/queue/reference/version-preservation oracle. Evidence there:
`artifacts/p1-hierarchical-root-set-second.log`,
`artifacts/p1-hierarchical-root-set-third.log`,
`artifacts/simulation/hier1648781653-root-set/` and
`artifacts/simulation/hier1878823561-root-set/`.

The canonical verifier `build/Verify.ps1 -Runtime win-x64 -SkipFuzz` passes
with exit 0 in 187.6505745 seconds on the production source above. All 1534
cases pass (983 Engine, 299 Storage), plus eight flat generated scenarios,
diagnostic/control/native/transport probes and win-x64 publishing. Unit tests
have no failures/skips. The separate boundary fuzz was explicitly skipped
locally; the previous green remote head does not validate this repair.
Evidence: `artifacts/p1-parent-root-set-canonical.log`,
`artifacts/verification-win-x64.json` and `artifacts/tests/`.

The [expanded hierarchy repair report](P1-hierarchical-root-set-repair-2026-10-20.md)
records a subsequent 20/20 fresh generated batch and all three checked-in
regression cases passing. The harness is now integrated with the repair for
one CI submission. The integrated canonical runner subsequently passes all
1534 unit and nine Simulation cases, eight flat/four hierarchy samples and
all three fixed regressions, probes and publishing in 454.194 seconds; see
the expanded report for exact source and artifacts. CI and merge remain
pending. The 1000-seed
CI/five-day and five-node 100000-file gates, installed service, LAN and native
NAT validation remain separate and unverified. No new remote CI run was
started for these local repairs.

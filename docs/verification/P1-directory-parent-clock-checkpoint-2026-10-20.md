# Directory parent clock checkpoint: integration still open

The exact-original-body repair closes newer known parent clock knowledge
before one local publication increment. It validates the same deterministic
body again under the final reference gate. Original/source/native/rebase
authority remains unchanged. New debug stages expose same-body clock-floor
construction and final clock validation without paths/hashes/envelopes.

Two focused owned/received baseline cases fail before repair. Both pass after
repair, along with three different-root/metadata exclusion cases and a late
receipt/rollback/retry control. A separate delayed 256 KiB response reproduces
the overly short one-second payload timeout, then passes after proportional
transfer allowance. Case routes now allow one second plus bytes at 256 KiB/s
(maximum 33 seconds for 8 MiB) inside a 300-second source attempt; small stalled
responses remain short and caller cancellation propagates. Route diagnostics
include requested bytes and elapsed milliseconds, without payload identifiers.

Source 53cd990 passes the complete canonical verifier:
`build/Verify.ps1 -Runtime win-x64 -SkipFuzz`, exit 0 in 177.6032576 seconds.
All 1512 canonical cases pass (972 Engine, 288 Storage), plus eight flat
scenarios, diagnostics/control/native/transport probes and win-x64 publishing.
No unit failures/skips. Local boundary fuzz was explicitly skipped. Engine
and Engine.Tests compiler/configured analyzers are clean at warning/error,
with no workspace warning or truncation; Storage gates were clean earlier in
this same source repair. Earlier isolated full Engine check: 972 passes in
35.475 seconds before the final logging-only additions.

Evidence: `artifacts/p1-directory-clock-baseline.log`,
`p1-directory-clock-focused.log`, `p1-directory-clock-guards.log`,
`p1-directory-clock-full-engine.log`, `p1-case-route-slow-baseline.log`,
`p1-case-route-slow-repair.log`, `p1-directory-clock-canonical.log`,
`verification-win-x64.json`, and `artifacts/tests/`.

Integration remains open. In the separate property worktree, source f23f9de
passes retained seed 1648781653 in 62.307 seconds. Seed 1044729930 still fails
in 116.759 seconds. Its retained native fixture is
`PeerSync-Simulation-ac098f597d184c8b836cc2cfe2bbd989` beneath the test TEMP
folder. After 24 rounds native trees differ and nodes 1/2/5 retain pending
case/publication work. One independently looping preserved root has exactly
the same current signed child on all six nodes, while parents alternate old
snapshot roots; their non-vector bodies differ only in snapshot hash. Both
original root member vectors are included by the current child vector. This
supports a bounded multi-root causal proof; it does not authorize merging
unproven roots. Follow `docs/proposals/P1-directory-parent-root-set-proof.md`.

The earlier requested 20-case batch failed with 15 passing cases, two retained
failures, one shrink and three skipped samples. It must not be called a green
20-case batch. No new PR #38 push/CI run or merge occurred during these local
repairs. All hierarchy, merged/CI, 1000-seed/five-day/100000-file, installed
service, LAN and NAT acceptance requirements remain open.

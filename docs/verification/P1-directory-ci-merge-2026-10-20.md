# Directory case preservation PR #38 merge audit

[PR #38](https://github.com/taskscape/PeerSync/pull/38) merged at
`2026-10-20T05:24:39Z`. Final head
`f821fb6b5aaa2bd8eb3539e8c2a0691fc22c9e6e`, the synthetic merge and actual
main squash `7b689d0f583835424f632983203e58049704dea9` all have exact tree
`0271d70475cb917498797cbfcea179772b507ed7`. The base was the expected
`6934850733ca74ba68c20351b1a5a0f088fb51cd`; all four review threads were resolved.

All five jobs in [run 39371411330](https://github.com/taskscape/PeerSync/actions/runs/39371411330)
pass on that exact head: x64 10m39s, ARM64 6m34s, Server 2022 11m08s,
Server 2025 11m11s and boundary/simulation 35m43s. Packaging/server jobs stay
within 15 minutes and the combined job within its measured 50-minute ceiling.
No check or job is removed. The preceding superseded run is cancelled evidence.

The downloaded actual Simulation TRX passes all ten methods, including all four
retained regressions and the eight-flat/four-hierarchy smoke. Native fuzz output
proves all four targets, 121880 seeds, 600.0023224 seconds and zero crashes.
Evidence is retained under `artifacts/ci38-final-evidence/`.

The old supervisor JSON still hardcodes `retainedRegressionCases=3`; this is a
stale summary field, not an omitted fourth case. Actual TRX proves four. Its
fuzz verification stopwatch also includes time waiting for Simulation; actual
fuzz duration is the native result above. The follow-up validation change uses
a shared method registry, verifies actual TRX scope and separates native fuzz
duration from supervisor elapsed time. Historical artifacts remain unchanged.

Main-push [run 39374744845](https://github.com/taskscape/PeerSync/actions/runs/39374744845)
passes all five jobs on the exact squash: x64 9m02s, ARM64 5m39s,
Server 2022 10m06s, Server 2025 13m22s and boundary/simulation 24m33s.
Downloaded main TRX again proves ten passed methods and all four retained
regressions; native fuzz proves 114074 seeds, 600.0007773 seconds and zero
crashes. Evidence is in `artifacts/ci38-main-evidence/`. Directory review,
payload route, parent-clock/root-set, tombstone-cycle and hierarchical harness
children meet their scoped acceptance. Their separate phase gates remain open.
Connected maintenance, final-source corpus, five actual CI campaign days and
production clock/service integration remain separate open acceptance. Existing
physical LAN/native NAT deferrals to P2/P4 are unchanged.

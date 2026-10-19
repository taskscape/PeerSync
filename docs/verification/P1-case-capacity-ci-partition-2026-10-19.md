# Partial case capacity and required CI partition (local checkpoint)

PR #36 head `00cd78b` passed all 1,130 cases on x64 before its job reached the
15-minute timeout during the following P0 probes. The other four required jobs
passed, including the unchanged 600-second fuzz and both complete Engine runs.
This was a duration failure, not billing or an assertion failure. Evidence:
[run 39295367628](https://github.com/taskscape/PeerSync/actions/runs/39295367628),
`artifacts/pr36-x64-cancelled.log` and downloaded `evidence-win-x64` TRX files.
The 1,001-path case test took 5m32.332s; the existing 1,001-block deletion test
took 3m24.448s. The Engine suite occupied 9m37s on this hosted VM. Local timings
are substantially shorter and do not predict hosted disk performance.

The five-job matrix now runs every Engine case on both required Server 2022
and Server 2025 jobs, and excludes its third duplicate execution from x64
packaging. All other suites, simulation, native probes, signing and publication
remain there. No boundary cases, durability guarantees, required jobs or fuzz
seconds were removed, and all time limits remain 15 minutes. The canonical
runner still runs Engine by default. Explicit `-SkipEngineTests` produces
`engineTestsRun: false`; partition success alone is not full workflow success.
The local partition run passed in 110.8889405 seconds, exit 0; its evidence is
`artifacts/p1-ci-partition-verification.log`.

A further capacity review exposed partial-batch starvation: unavailable older
sources can leave fewer free slots than the next recomputation batch needs.
Rolling back that whole batch prevents the newly available sources from ever
draining. A Storage regression expected 999 admitted sources but saw 998; a
native Engine regression retained ordinary needed work after eight passes.
After correcting uppercase fixture key comparisons, both fail on the original
production implementation, then pass with the repair.

Recomputation now uses a per-path savepoint. Only typed capacity saturation
rolls back the failing path and commits the valid prefix FULL before reporting
backpressure. A path needing two sources cannot retain only its first source.
Other exceptions or cancellation roll back the current entire batch; remote
receipt still commits all records, sources, work and cursor together or none.
The native regression retains 999 valid signed historical sources with persisted
retry deadlines while four independent new case paths preserve all eight visible
versions and drain ordinary work. It does not weaken the original 1,001-path test.

All 13 focused Storage and 27 native case-recovery cases pass. Scoped Storage,
Storage.Tests and Engine.Tests compiler/configured analyzer checks are clean
without truncation or load warnings. Baseline and passing logs/TRX are retained
under `artifacts/p1-partial-capacity-*` and `artifacts/tests/partial-capacity*/`.
The final canonical `build/Verify.ps1 -SkipFuzz` passes all 1,134 cases,
native/probe/watcher/QUIC/publication checks in 129.6709532 seconds, exit 0,
with `engineTestsRun: true`: 134 Core, 15 Crypto, seven Protocol, nine Net,
720 Engine, 218 Storage, 28 Contracts and three Simulation methods (eight
property scenarios). Its log is `artifacts/p1-partial-capacity-verification.log`;
simulation evidence is `artifacts/simulation/20261019T180810-9ed2dd007f35422d84e022aeccc54343/`.
Boundary fuzz was explicitly skipped locally. Final-head hosted/main checks
and thread resolution remain pending; no phase gate or subtree work closes.

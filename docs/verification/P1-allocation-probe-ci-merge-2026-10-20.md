# Allocation diagnostic PR #40 merge audit

[PR #40](https://github.com/taskscape/PeerSync/pull/40) merged at
`2026-10-20T08:00:29Z`. Submitted head
`a9748eb1cdd2567ade615c9101d81436efde9cc1`, tested synthetic merge
`1a825b39a75cac59aa224ae59bd52d5641c2c359` and actual main squash
`a4d9529fa283a6af5dad5f074d0098cd84e8202b` have the identical tree
`5eb6da7aa11f31325ac0334b6d90a8df92f0e7e6`. The expected base was
`bf68d97743167730722913672b2562e1e86cf183`. The review completed successfully
and the final review-thread query contained no findings. No unchanged run
was restarted.

All five jobs in [PR run 39385945601](https://github.com/taskscape/PeerSync/actions/runs/39385945601)
pass: x64 6m39s, ARM64 3m56s, Server 2022 10m45s, Server 2025 12m06s
and boundary/simulation 36m06s. The actual campaign-control step passes
all 28 tests. The final artifact scope validator confirms eleven Simulation
methods, all four retained regressions and connected maintenance. Maintenance
retires two native archives and evicts two blocks. Native fuzz records
108812 seeds, 600.0003707 seconds and zero crashes; supervisor elapsed time
2043.1003528 seconds is separate. Evidence is retained under
`artifacts/ci40-final-evidence/` and `artifacts/ci40-fuzz-job.log`.

Main-push [run 39390329140](https://github.com/taskscape/PeerSync/actions/runs/39390329140)
fails on the exact squash: ARM64, x64, Server 2022 and Server 2025 pass,
but boundary/simulation fails on generated hierarchy seed 1608819303.
Its matching native trees and empty needed queues do not converge to stable
index high-waters within the unchanged 24-round bound. The other ten actual
Simulation methods pass, including all four retained regressions. Separate
boundary fuzz completes 164704 seeds in 600.0033767 seconds with zero crashes.
Raw failed-run evidence is retained in `artifacts/ci40-main-failed-evidence/`
and `artifacts/ci40-main-failed.log`. This is a product convergence failure;
the diagnostic and campaign remain inactive pending repair and green main.
Downloaded x64
TRX proves 551/551 unit results across eight projects, with no failed results;
Engine tests run separately in the required Server jobs. The x64 verification
manifest explicitly records `engineTestsRun=false`, `simulationRun=false` and
`gateP1Passed=false`; its 401.0870397 seconds do not prove those separate checks.
Raw evidence and counted results are retained in `artifacts/ci40-main-x64/`
and `artifacts/ci40-main-x64-unit-counts.json`. Downloaded Server 2022 TRX
independently confirms 994/994 Engine cases, bringing the distributed main
unit evidence to 1545 passed results. Those raw results and counts are retained
under `artifacts/ci40-main-server2022/` and
`artifacts/ci40-main-server2022-unit-counts.json`. All five required jobs and final artifacts
must pass before dispatching the manual allocation diagnostic. Actual hosted
cached-planner rerun proof remains unverified; P1.6.4a and P1.6.4a1 stay open.
The separately completed hosted preflight is recorded in its own report and
does not qualify as a campaign day. Activation variables remain unset.

This follow-up changes documentation and validation controls/workflows only.
Product source, build tooling and Benchmark C# sources match the immutable
`bf68d97743167730722913672b2562e1e86cf183` functional source. The active
100000-file runs keep their original worktrees and binaries. Neither a
passing full-corpus report nor five consecutive CI campaign dates is claimed.

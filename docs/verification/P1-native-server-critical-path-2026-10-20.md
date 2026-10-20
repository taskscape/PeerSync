# Native Server runner: local critical-path repair

[PR #41 run 39411518907](https://github.com/taskscape/PeerSync/actions/runs/39411518907)
checks submitted head 4c574b9fcbdca1890d98214c7d91d53d823a4100 through synthetic
merge 1dc1da1598649f2df9eb703619e6d26b68559b2d. The x64, ARM64 and Server 2022
jobs pass. Server 2025 fails its Engine child's eleven-minute deadline;
simulation/fuzz is still active at this checkpoint. This run cannot qualify
the PR or permit a merge.

The completed Server 2025 job log is available through the job-log API while
the overall run is active. Its native P0/cross-account summaries pass, but no
Engine TRX completes. ANSI-stripped stdout proves 934 unique passing results
and no reported assertion failure. Comparison with the same source's complete
Server 2022 TRX leaves 68 unfinished required cases: 21 BlockRetention,
43 TypeTransition and four HistoryPolicy. Do not count these as passes. The
Server 2022 artifact proves all 1003 Engine results and 24 exact directory
clock/authority/graph/revival controls; x64 contributes 551 other unit results.
Evidence: `artifacts/ci41-revival-server2025-job.log`, corresponding downloaded
artifacts, `ci41-revival-server2025-timeout-scope.json` and the actual successful
Server 2022/x64 TRX and scope/count JSON.

The old Server runner spends approximately 126 seconds building the entire
solution before starting its fixed 660-second Engine timer. The new explicit
`build/PeerSync.NativeVerification.slnx` contains seven direct projects:
Engine.Tests and all six P0 spikes, including WatcherStress. Independent XML
reference traversal verifies the complete fourteen-project dependency closure,
all required probe names and no WPF project, against the original 33-project
solution. All immutable test/spike outputs still build before Engine starts;
the normal canonical and packaging build scopes are unchanged.

The revised runner measures one 780-second verification budget beginning before
the focused locked build. Build and independent probe time reduce its remaining
Engine wait. It rejects exhausted builds, exhausted waits and completion outside
that budget. Existing per-probe deadlines, owned-process cleanup and the outer
fifteen-minute workflow ceiling remain active; the outer ceiling still bounds
a probe that has not returned to the script's budget check. Success/failure
evidence records build, remaining-wait and total timings. All five jobs, every
native/service probe and the existing single Server 2025 capacity duplicate
exclusion remain unchanged.

Before implementation, design/decision/WIP commit
5a72361fb13ece6db4cc225b89393e0353626c8d precedes the measured unchanged local
runner. It passes all 1003 Engine results plus native/account probes: 20.93
seconds reported build time and 51.3862948 seconds after build (50.810 seconds
test-process time). The revised runner then passes after cleaning its focused
dependency graph: build 6.8699765 seconds, after-build 51.7757893 seconds, total
58.6570578 seconds, all 1003 Engine results (51.153 test-process seconds) and
native/account probes. These workstation runs do not reproduce Server 2025's
slow execution and are not a controlled cold-NuGet benchmark or hosted proof.
Snapshots under `artifacts/p1-server-budget-{before,after}-evidence/` preserve
the actual summaries, environment, stdout/stderr and TRX; console logs and
`p1-server-budget-build-closure.json` retain the procedure.

A separate generated operational fixture changes only its output directory
and total budget to one millisecond. The real build then exhausts that budget,
returns exit 1, records failure with no Engine child started and no child
stdout. This verifies the early-exhaustion path without weakening production
scope. Evidence: `artifacts/p1-server-budget-exhaustion*`. PowerShell syntax,
solution parsing and whitespace checks pass. No C# product source changed;
current-product scale checks remain bound to their actual 4c574b9 source and
binary hashes. Updated hosted Server 2025 and complete PR/main acceptance remain
mandatory; P1.6.5 and the phase gate stay open.

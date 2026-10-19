# Native probe readiness and duplicate watcher isolation

PR #37 head `2ce4306` passes x64, ARM64 and the unchanged required fuzz job,
but both Server jobs fail in [run 39308038324](https://github.com/taskscape/PeerSync/actions/runs/39308038324).
Server 2025's same-user pipe client exits 1 while the server eventually prints
READY; the old spike prints only RpcException/HRESULT, so its exact nested cause
cannot be recovered from that run. Server 2022 passes the pipe checks, then its
100,000-operation watcher exceeds the unchanged 180-second deadline while the
complete Engine suite competes for hosted disk I/O. Neither failure is billing.
Logs/artifacts are in `artifacts/pr37-review-server202{2,5}.log` and
`artifacts/ci-pr37-review-server2025/`. No blind rerun was started.

Run-P0 previously slept one second after starting ControlPipe. It now waits for
a fresh readiness JSON file bearing the actual still-running server PID. Files
are unique per invocation; a wrong PID or partially written JSON cannot release
the client. The spike writes readiness only after Kestrel starts. The connection
deadline remains three seconds and the RPC budget ten seconds. Owner and caller
checks and the negative-owner/cross-account cases are unchanged. The pipe client
also disables HTTP proxy/redirect routing and reports bounded sanitized RPC/native
causes without raw exception text or identities.

The real delayed-server regression reproduces early connection timeout after the
old one-second wait. A second real server delays five seconds, overwrites a
deliberately wrong-PID readiness file, and then passes the actual owner/caller
RPC. The fixture delay is a bounded optional P0-spike argument, not a production
service/client override. `build/Test-ControlPipeReadiness.ps1` retains its unique
evidence under `artifacts/p0/pipe-readiness/` and is included in the default x64
canonical runner. Scoped ControlPipe compiler/configured analyzers are clean
without truncation/load warnings; Release build and the regression pass.

The complete watcher stress remains mandatory in the required x64 canonical CI
job and default local runner. Both Server jobs omit only its duplicate execution,
record `watcherRuntimeChecks: false` plus its required x64 coverage, and retain
every other native probe, complete Engine suite and real cross-account checks.
The workflow still has five required jobs, 600-second fuzz and 15-minute limits.
The Engine process still overlaps independent non-watcher probes, with bounded
wait/owned-child cleanup and fresh run-specific outcomes. A partial Server probe
summary is not a complete watcher-validation claim.

Evidence: `artifacts/p1-probe-readiness-{build,test}.log` and unique readiness
summaries. Final canonical `build/Verify.ps1 -SkipFuzz` terminates exit 0 with
all 1,176 cases, the readiness regression, complete 100,000-operation watcher,
other native/probe/QUIC/publication checks in 167.4118421 seconds. Its log is
`artifacts/p1-control-readiness-final-verification.log`; property evidence is
`artifacts/simulation/20261019T192303-34af55ff46b84d5d973cdccdcdf7e823/` (eight
scenarios/three methods). Local boundary fuzz is explicitly skipped.

The final Server runner also exits 0: run `4a4f5042a23e49558842c5746d7f0872`,
38.3854439 seconds after build, all 720 Engine cases, every non-watcher native
probe and real virtual-account/private-key/standard-user checks pass. Its summary
explicitly records the watcher omission and required x64 coverage. Evidence:
`artifacts/p1-service-readiness-final-verification.log`,
`artifacts/p0-services/parallel-summary.json` and `artifacts/tests/native-engine/`.
The two temporary services are absent after cleanup. These are local timings,
not hosted performance predictions. Final-head CI/main and review resolution
remain pending.

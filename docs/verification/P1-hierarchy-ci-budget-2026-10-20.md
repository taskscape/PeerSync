# Hosted hierarchy deadline: no assertion failure established

PR #38 source c5301820b7b723828375e31f57e5528b96f8ea96 ran in
[39361484050](https://github.com/taskscape/PeerSync/actions/runs/39361484050).
Arm64 publication/signing, 600-second boundary fuzz and both Server native/
security jobs pass. The x64 packaging job is cancelled after 20 minutes.
Its exact annotation is: `The job has exceeded the maximum execution time of
20m0s`. The build/test step starts at 02:56:02 UTC and is cancelled at
03:15:02; setup consumes the preceding minute of the job budget.

All 551 non-Engine unit cases pass; full Engine coverage belongs to the two
required native jobs. Simulation starts at 03:00:07 UTC. Its preserved TRX
has seven executed/passed cases, zero failed cases. Both requested generated
batches complete: eight flat samples in 42.695 seconds and four hierarchy
samples in 465.878 seconds. The fixed six-node seed 1044729930 passes in
340.567 scenario seconds. The next fixed seed 1878823561 is cancelled during
operation 14; its saved error is OperationCanceledException, not a convergence
or preservation assertion. Seed 1648781653 has no completed result. These
two fixed cases and the job are not passing evidence.

The always-upload evidence step succeeds. Preserve its artifact and generated
cancellation fixture; no speculative engine repair or unchanged-job retry is
justified by this timeout. Local 1534-unit/nine-Simulation canonical and the
fresh 20-case hierarchy results remain distinct evidence.

Artifacts in the primary checkout: `artifacts/ci38-x64-timeout.log`,
`artifacts/ci38-x64-job.json`, and `artifacts/ci38-x64-evidence/` downloaded from
the run's `evidence-win-x64` artifact. The partial Simulation TRX and
`simulation/20261020T030005-05c4301045c045c4877452f42270cc66/` contain the
sample summaries, completed six-node result and the seven-node cancellation
replay/fixture ZIP.

The repair changes only the x64 timeout to 40 minutes and restores Arm64's
15-minute ceiling. All tests, three fixed seeds, generated counts, signing,
probes and other four jobs retain their existing scope. No new job or schedule
is added. This ceiling allows the observed slower hosted native/SQLite work;
it does not require the full duration to be consumed. Final repaired-head CI,
merge and main CI remain pending.

## Second hosted measurement

Run [39363534643](https://github.com/taskscape/PeerSync/actions/runs/39363534643)
on ad37f8d reaches the 40-minute ceiling during publishing. All four other
jobs pass again. Its preserved Simulation TRX has nine executed/passed cases,
zero failures or skips. Eight flat samples take 59.278 seconds; four hierarchy
samples take 703.525 seconds. Fixed seeds 1044729930/1648781653/1878823561
pass in 509.65/266.10/358.85 scenario seconds. No replay failure is produced.
All QUIC/pipe/rendezvous probes, 100000-operation watcher and QUIC diagnostic
controls pass. Cancellation occurs after restore in the publishing phase;
signing/bundle completion and the overall job are not passing evidence.

Preserved primary artifacts: `artifacts/ci38-x64-second.log` and
`artifacts/ci38-x64-second-evidence/`, including
`simulation/20261020T032329-e93cc9a6459341c6915aab143681be79/` and the nine-case
Simulation TRX. This establishes complete hosted simulation acceptance on
that source, independently of the unsuccessful packaging job.

Move that unchanged simulation workload out of packaging and alongside the
required 600-second fuzz in its existing job. Build both outputs before either
starts, execute only immutable binaries and preserve both results even on
failure. Fuzz duration/targets/seeds/crashes remain strictly validated. Default
local canonical verification still runs all tests; only packaging explicitly
delegates Simulation coverage to the boundary/simulation job. Both packaging
jobs return to 15 minutes; boundary/simulation gets 50 minutes based on the
observed 32-minute suite plus setup/cleanup headroom. This preserves five jobs
and every check while overlapping the independent workloads. Local overlap
validation and the repaired-head CI/main remain pending.

## Local overlap and delegated packaging verification

The new boundary/simulation supervisor passes with exit 0 in 614.974565
seconds. Actual fuzz output proves 600.003851 seconds, 190444 seeds, zero
crashes and the required FastCDC/PathRules/VersionVector/IgnoreMatcher targets.
All nine Simulation cases pass alongside it, including eight flat/four
hierarchy samples and all three retained regressions. Both output sets were
built before either process started; no binary rebuild/publish occurred
during their lifetimes. The supervisor owns cancellation/drain/disposal and
preserves both evidence sets.

The corresponding packaging command
`build/Verify.ps1 -Runtime win-x64 -SkipEngineTests -SkipSimulation -SkipFuzz`
passes with exit 0 in 148.8558336 seconds: all 551 owned unit cases, native/
transport/control/diagnostic probes, 100000 watcher operations and win-x64
publishing. Its summary explicitly reports `engineTestsRun=false` and
`simulationRun=false`; the existing two Server jobs own Engine coverage and
the boundary job owns the unchanged Simulation suite. Default local Verify
still runs both scopes when the explicit switches are absent.

Primary evidence: `artifacts/p1-boundary-simulation-overlap.log`,
`artifacts/fuzz/boundary-simulation.json`, `artifacts/fuzz/summary.log`,
`artifacts/fuzz/verification.json`, `artifacts/p1-delegated-packaging.log`,
`artifacts/verification-win-x64.json`, and
`artifacts/simulation/20261020T040442-a4269de8961648a0bc617aae0f44b041/`.
Local assertions and overlap are verified; repaired-head CI, merge and main
CI are still required. No source-level engine change follows the hosted
timeout, and no existing test is dropped.

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

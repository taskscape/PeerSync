# Isolate P2 boundary Simulation cohorts on hosted runners

PR #44 run [39767094436](https://github.com/taskscape/PeerSync/actions/runs/39767094436)
reached the unchanged 2,700-second boundary supervisor limit. The five-target
fuzzer completed 600.001 seconds, 94,470 seeds and zero crashes. The seven
baseline Simulation methods passed; one retained cohort passed its two seeds.
Two other retained processes were stopped by the supervisor, and only five of
eight required retained result files exist. No failed product assertion is
recorded. The baseline alone took 42m26s while four Simulation workers shared
one hosted machine. This is a resource-contention result, not a complete gate.
The same two retained-3 methods and their evidence package passed on the
current local source in 83.465 seconds without the other Simulation workers;
the hosted result took 24m53s under shared-runner contention. Machine speeds
are not equivalent, so the local timing is diagnostic only.

The earlier [bounded-cohort decision](P1-bounded-ci-simulation-cohorts.md)
explicitly forbids adding jobs. Its single-runner two-cohort layout and the
subsequent four-cohort layout both exhausted the same 45-minute internal
budget, so a change to that decision is required before another hosted retry.

Run the ten-minute fuzzer and seven baseline methods in the existing
`fuzz-smoke` job. Run the eight exact retained seeds in three disjoint cohorts
on three separate Windows hosted runners. Keep each job's 2,700-second
script budget and 50-minute Actions cap. A final aggregation job downloads
fresh artifacts from only this workflow attempt and verifies matching source
revision and binary identity, each job's positive status/counters/evidence,
all 15 unique passed Simulation methods, eight retained result files,
eight flat/four hierarchy results, connected maintenance, and the actual
fuzz duration/targets/zero crashes. Missing, duplicated, failed, skipped,
mixed-attempt or mismatched-source evidence must fail aggregation. The
original five jobs become nine physical jobs (four boundary-related jobs
plus one aggregator in place of one boundary job); no existing test case,
seed, healing round, oracle, native check or security job is removed.

Implement local positive/negative controls for shard ownership, manifest
identity, missing/duplicate/failed results, and aggregate coverage before a
single hosted rerun. Preserve the three incomplete PR runs as failed evidence.
The draft controls currently pass 23 exact-scope checks, eleven aggregate
artifact checks and three supervisor/owned-cleanup checks; the real local
retained-3 shard passes both methods and writes a complete package.
Do not mark Gate P2 or begin P3 until the P2 service repair and the new
current-head CI pass, PR #44 is merged, and main CI is green. Physical P2
validation remains unchecked under the separately approved phasing decision.

This change uses four extra Windows runners versus the previous five-job
layout, but keeps a finite per-run budget and avoids repeating a job already
shown to time out from contention. GitHub Actions billing and available
budget remain observable in the hosted run; local checks remain authoritative
when hosted credits are unavailable, without reclassifying required CI as
passed.

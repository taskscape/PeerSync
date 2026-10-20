# Campaign rerun budget review repair

The planner's attempt check is insufficient: GitHub can reuse a successful plan
when rerunning only failed jobs. Downstream campaign shards/report must reject
attempts after one in their job-level allocation conditions. The plan job must
also reject campaign reruns before allocating its Linux runner. Explicit manual
preflight reruns remain non-gating and supported.

Daily campaign evidence must require integer runAttempt=1, not merely uniform
attempt numbers across shards. Standalone history validation must independently
require integer attempt 1 for every report, because it does not re-run daily
validation. Preflight may accept uniform later attempts while preserving its
non-gating label.

Add failing controls for uniformly rerun shards/history, actual job-expression
allocation with cached successful planner outputs and first-attempt/preflight
positive cases. Preserve scope/count/retirement/source/native checks. Run focused
planner/evidence/allocation controls and validate workflow syntax before one
batched PR update. Do not activate schedules or broaden resource bounds.

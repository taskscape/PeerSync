# Bounded P1 thousand-seed CI campaign

Gate P1 needs actual CI evidence for 1000 seeds on five consecutive days.
Local 1000-case runs, bounded PR smoke and one successful day do not satisfy
that gate. Keep the current documented modes explicit: 1000 distinct flat
seeds per campaign day, five generated hierarchy samples and all four
retained hierarchy regressions. This does not claim 1000 hierarchical cases.
Preserve the existing recursive native/version/reference/queue oracles.

Use an opt-in dedicated workflow with five Windows shards of 200 flat cases
and one hierarchy sample each. Only shard zero repeats the four retained
hierarchy regressions; other shards omit only those duplicate cases. Default
local verification and the existing five-job PR/main workflow retain every
case. Do not put the 100000-file corpus into hosted/default validation.

Each shard checks out the same immutable source commit, uses locked restore,
builds Simulation once and emits source/run/shard/date/count metadata alongside
its existing case summaries, TRX and failure fixtures. The coordinator requires
all five successful shards, exactly 1000 distinct completed flat seeds with
3–7 nodes, each hierarchy sample and all four retained results. Missing,
cancelled, malformed, duplicate or mixed-source evidence is a failed day.
Preflight mode runs only ten flat/five hierarchy samples without the retained
long cases, labels itself non-gating and never counts as a campaign day.

Scheduling is disabled by default. The scheduled job's runner-level condition
requires a configured source SHA and numeric last permitted workflow run
number. Activation pins an already green P1 source and permits only the next
five attempts. After that run number, the workflow allocates no runners.
Manual preflight cannot silently extend the campaign; document that run
numbers include preflights and that activation must calculate its bound from
the current workflow counter. There is no automatic budget extension after a
failure. Keep activation separate from implementation and perform it only
when the required P1 implementation source is merged and green.

Require a history audit of five successful campaign reports from distinct,
consecutive UTC completion dates on the same pinned source. Do not infer
history from workflow status alone, count preflights/rerun attempts as days,
or reset a failed day without preserving its evidence. Disable/retire the
campaign schedule after acceptance. Existing job/time/coverage evidence stays
separate from required main CI, full corpus and external network validation.

Validate the evidence coordinator with generated positive/negative manifests,
including duplicate seeds/shards/days, missing regressions, wrong source,
partial/cancelled results and non-consecutive dates. Then run a small local
shard and hosted preflight before activation. Publish the exact resource
limits and actual scope/counts; no phase or checkbox closes from preflight.

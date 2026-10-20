# P1 bounded campaign tooling - 2026-10-20

Status: local coordinator preflight passes; implementation remains unmerged and
inactive. Hosted preflight and five actual campaign days are not yet verified.

The dedicated workflow retains the normal five-job PR/main CI layout. Its planner
checks immutable source existence, source-bound shard tooling, merged ancestry and
all five successful normal CI checks before campaign activation. Without explicit
source and last-run variables, scheduled planning allocates no runner. Attempts are
bounded to five workflow run numbers; reruns and source overrides cannot extend
that bound. Each attempt has five Windows shards (200 flat seeds and one hierarchy
sample each), with all four retained regressions owned by shard zero. Existing
production native/loss/reference/queue oracles remain unchanged.

A full campaign has maximum failure ceilings of 75 + 4*50 = 275 Windows runner
minutes and two five-minute Linux jobs per attempt. Five attempts therefore have
ceilings of 1375 Windows and 50 Linux runner minutes, excluding the explicitly
separate hosted preflight and ordinary CI. These are resource ceilings, not actual
runtime or monetary prices. No activation variables have been written.

Sixteen Python control tests pass: complete synthetic manifests, duplicate seeds/
shards, partial/cancelled results, exact TRX method scope, missing retained results,
source/run/attempt mixing, booleans, artifact path escapes, retained registry drift,
nonconsecutive days and source/activation bounds. Synthetic 1000-seed manifests
are validator controls only. YAML parsing/structure confirms only manual/scheduled
triggers, the disabled schedule guard, five shards and fail-fast=false. The planner
runs these controls before allocating Windows shards. Hosted workflow syntax and
execution remain pending; local parsing alone does not prove them.

The complete coordinated local preflight passes all five shards on immutable
commit `708ef056a5db044cc104599cff62b28c02883bf7`. Its ten distinct flat seeds
preserve 90 published versions, with five actual renames and nine deletions.
There are five generated hierarchy samples; every shard executes exactly the six
baseline methods. Retained regressions are deliberately omitted in preflight.
The coordinator records `qualifiedCampaignDay=false` and `gateP1Passed=false`.
Evidence: `artifacts/p1-campaign-local-preflight-20261020T045032/day.json`, the
five copied shard directories, their actual TRX, source/binary hashes and logs.
`-NoBuild` is permitted only for this non-gating preflight; production/test C#
matches PR #38's fully verified repair, while campaign code is separate tooling.

Earlier local attempts remain partial: source819f0fd stopped at hierarchy seed
1583987140, which led to the repaired permanent regression. A later attempt on
c10739c passed shard zero before a local workflow commit changed HEAD; the source
guard rejected the next shard. Neither is a day or a coordinated passing preflight.
No failed source metadata is rewritten into passing evidence.

The clean-worktree full-shard check and expanded seven-method preflight are
completed in the addendum below. They remain local evidence and cannot satisfy
the CI/five-day gate. Final CI/main, hosted
preflight, live run/attempt history audit and separate 100000-file corpus remain
mandatory before any relevant P1 checkbox is closed.

## Expanded maintenance scope and actual clean full-shard proof

The final scope adds connected production maintenance as a seventh baseline
method. Each actual shard must prove native archive retirement/block eviction,
with its maintenance JSON bound to the executable hash and (campaign mode)
compiled source pin. All seventeen planner/evidence controls pass. Normal CI's
existing required boundary job owns these controls; no sixth job is added.

On clean-built source `627d5420f0772c836d002d17808fb585c06d3490`, full local
shard zero passes 200 distinct flat seeds, one hierarchy sample, all four retained
regressions and connected maintenance: eleven methods, no failed/skipped results.
It runs from 05:22:47.8804210 to 05:32:08.3863894 UTC. Copied evidence:
`artifacts/campaign-maintenance-full-627d542/shard-0/`, actual TRX, log and
`local-shard-check.json`. This is local full-shard evidence, not a CI day.

All five fresh local preflight shards then pass on that same immutable source:
ten distinct flat seeds, five hierarchy samples, five maintenance cases, ten
retired native archives and ten evicted blocks. The actual seven-method scope
is verified for every shard. The current shared-registry coordinator revalidates
all copied artifacts at `artifacts/p1-maintenance-preflight-627d542/`, producing
`shared-registry-day.json` with `qualifiedCampaignDay=false` and
`gateP1Passed=false`. Earlier six-method reports remain historical scopes.

The scope registry is shared by Python campaign validation and PowerShell CI
summary validation. Replaying the actual eleven-method TRX returns four retained
and one maintenance case; the older ten-method TRX is correctly rejected for
the expanded scope. Native fuzz duration and supervisor elapsed time now have
separate fields. The changed supervisor's authoritative execution is still
required in follow-up CI; its pure scope reader was tested against real TRX.

Full combined canonical verification passes in 611.3623527 seconds: 1545 unit
cases, eleven Simulation methods, all four retained regressions, native/probe/
publishing, and actual 600.003059-second fuzz (182768 seeds, zero crashes).
Scheduling remains inactive; hosted preflight, final-source full corpus,
merged green CI and five actual qualified CI dates remain open.

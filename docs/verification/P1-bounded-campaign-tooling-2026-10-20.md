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

A clean-worktree full shard check is being prepared to exercise actual build,
clean source provenance, 200 flat cases and all four retained results. It is still
local evidence and cannot satisfy the CI/five-day gate. Final CI/main, hosted
preflight, live run/attempt history audit and separate 100000-file corpus remain
mandatory before any relevant P1 checkbox is closed.

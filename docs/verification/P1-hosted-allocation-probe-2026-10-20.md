# Hosted allocation probe: local implementation

P1.6.4a1 remains open for actual hosted reruns. The dedicated workflow is manual,
Linux-only and contains no checkout, product execution, artifact upload or API
mutation. Three jobs each have a one-minute ceiling. CI compares every job
allocation expression exactly with the production campaign, preventing drift.

The planner emits synthetic active/mode and original run/attempt context, then
succeeds. The shard logs actual context and deliberately fails attempt one.
The always-report branch records the expected negative result. A failed-job
rerun must reuse that successful planner. Campaign attempt two must skip fresh
shard/report execution; preflight attempt two must execute both successfully
with the original planner's attempt/run identity. Every diagnostic log labels
qualifiedCampaignDay=false/gateP1Passed=false. Two modes and one failed-job rerun
each bound the test to eight possible Linux allocations; no Windows campaign
run number, activation variables or native oracle changes are involved.

Before implementation, the two added controls reproduce four missing-workflow
errors/subcases while the existing 26 controls pass. After implementation all
28 controls pass, zero failures, exit 0 in 0.654 seconds. YAML parsing and syntax
checking of all three embedded Python programs pass. Evidence is retained in
`artifacts/p1-hosted-allocation-before.log`,
`artifacts/p1-hosted-allocation-after.log` and
`artifacts/validate-allocation-probe.py`. `git diff --check` passes.

No C# source, project, package, product build or native test changes accompany
this probe. Existing canonical and current main unit/native evidence remain
applicable to those unchanged sources; the new 28 controls also run in the
normal required CI boundary job. Actual hosted scheduler behavior, exact
per-attempt job identities/steps and new PR/main acceptance remain pending.
The source/proposal/WIP commit preceded implementation. P1.6.4a and its new
child remain unchecked until the required proof is retained.

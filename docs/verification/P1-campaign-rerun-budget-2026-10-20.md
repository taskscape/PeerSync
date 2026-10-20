# P1 campaign rerun resource-bound review repair

Three PR #39 review findings identify a single enforcement gap: failed-job reruns
can reuse successful planner outputs. Planner-only attempt validation does not
bound downstream runner allocation, uniformly rerun daily evidence was accepted,
and standalone history validation independently omitted the attempt check.

Before repair, 26 controls produce eight failures (including subcases) for actual
workflow allocation expressions and uniformly rerun day/history evidence.
After repair, all 26 pass. The allocation controls parse the repository's actual
job expressions with a restricted AST interpreter and verify cached successful
planner outputs, scheduled/manual campaign reruns, first attempts, inactive/expired
schedules and explicit manual preflight reruns. They do not copy the production
planner's decision logic. Python control evidence: `artifacts/p1-rerun-budget-before.log`
and `artifacts/p1-rerun-budget-after.log`.

Campaign plan, shard and report jobs all require attempt 1 before allocating
runners. The producer rejects campaign attempt 2 before restoring/building.
Daily campaign validation requires integer attempt 1 in each shard; standalone
history independently requires integer attempt 1 in every report. Uniform later
preflight attempts remain non-gating. These guards preserve source, method,
maintenance, date and mutation requirements and do not extend the five-run bound.

An actual local attempt-2 preflight passes two flat seeds, one hierarchy sample,
connected maintenance and all seven baseline methods. Its manifest records
`mode=preflight`, `runAttempt=2`, status passed and no retained-regression ownership.
Evidence: `artifacts/p1-preflight-rerun-positive.log` and
`artifacts/simulation/20261020T060346-9b73ac49fc0d47e5a091abd407b30ece/`.
A direct attempt-2 campaign producer invocation is rejected before build.
YAML parsing and actual allocation-expression controls pass; hosted failed-job
rerun behavior remains part of post-merge workflow verification.

No C# source or production runtime behavior changes in this repair; the full
1545-unit/eleven-Simulation/600-second-fuzz canonical evidence remains applicable
to those unchanged sources. Updated required CI/main acceptance remains pending.
Scheduling is inactive. Review threads are not resolved before repaired-head
CI evidence. No budget, phase or external-validation gate is silently closed.

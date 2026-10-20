# Hosted cached-planner allocation proof

The rerun repair report keeps hosted failed-job rerun verification open. Local
AST evaluation proves the expressions' intended values, but not GitHub's cached
successful planner behavior. Verify that behavior separately from qualifying
campaign days using a manual Linux-only diagnostic workflow.

Copy the production plan/shards/report job allocation expressions exactly and
make normal CI reject drift. Keep all three jobs on ubuntu-latest with one-minute
ceilings, no schedule and no product/test/artifact or activation-variable writes.
A synthetic planner succeeds with active=true, selected mode, original run ID
and attempt. The shard logs actual allocation and deliberately fails attempt one;
the report logs context with qualifiedCampaignDay=false/gateP1Passed=false.

Dispatch one campaign diagnostic and one preflight diagnostic after the workflow
is merged. Re-run only failed jobs once for each. Verify the successful planner
is reused, including its original job identity/attempt. Campaign attempt two
must contain no newly executed shard/report steps. Preflight attempt two must
execute both jobs successfully with the cached attempt-one planner outputs.
Retain run IDs, source SHA, per-attempt jobs/steps and conclusions; source/mode
labels and these observations must agree. Do not count these as campaign days.

This uses eight one-minute Linux job ceilings across the two first attempts
and the positive rerun; the negative rerun must allocate zero fresh runners.
No Windows campaign attempt or workflow run number is consumed. Run exactly
the two diagnostic modes and one failed-job rerun each; do not auto-retry errors.
Existing five-attempt campaign bounds and every native preservation oracle stay.

GitHub documents [failed-job reruns](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs?tool=cli)
and [per-attempt workflow/job metadata](https://docs.github.com/en/rest/actions/workflow-runs).
Original deliberately failed diagnostics remain preserved as expected negative
evidence. Normal PR/main CI must stay green before the probe is used.

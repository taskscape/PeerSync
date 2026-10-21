# Proposed reporting-only recovery for October 21

Status: proposed, pending user approval. The user has already approved the
two-day duration; this separate proposal concerns the hosted reporter's
budget-denied startup, not test execution or calendar duration.

The campaign runbook requires reports from distinct successful run IDs and
authoritative GitHub conclusions. October 21's original overall workflow
is failed. Its planner and all five test jobs succeeded, with 41 actual
passing methods/1000 distinct flat seeds/five hierarchy samples/all six
regressions/five maintenance cases. The final reporting job has runner ID 0,
no steps and GitHub's explicit Actions-budget-denial annotation. It did not
execute any tests or produce a coordinator report.

The [complete evidence report](../verification/P1-campaign-day-2026-10-21.md)
preserves the failed overall conclusion and original metadata. Running the
exact pinned-source daily verifier locally over the five actual artifacts
passes; current daily validation logic is unchanged.
Both real UTC dates pass the updated two-report history check, with the same
green merged source and identical actual Simulation binaries. No data or
case count is synthesized.

Proposed narrow exception: accept this locally reconstructed reporting
result as the October 21 campaign test date only after authoritative audit
proves the planner and every required hosted test job succeeded on attempt
1, full artifact/method/source/count/date checks pass, and the sole failure
is a report job denied before runner allocation. Keep the failed workflow
conclusion visible and attach both the denial and reconstructed report.
Failures in any test, missing artifacts, reruns, mixed source, unverified
counts or an executed failing reporter cannot use this exception.

The two-day requirement, unchanged production oracles, full corpus and
ordinary green PR/main CI requirements remain mandatory. No additional
product campaign is dispatched and no ceiling is extended. Approval permits
recording the reporting exception and retiring this completed two-attempt
campaign; it does not waive budget-blocked validation of the new tooling.

Alternative: require a separately hosted recovery report after the Actions
budget is restored. Preserve the original attempt and use the same actual
test artifacts; do not rerun the thousand-seed jobs merely to regenerate
reporting. The existing campaign rerun guard intentionally prevents treating
a failed-job workflow rerun as a new qualifying first-attempt day.

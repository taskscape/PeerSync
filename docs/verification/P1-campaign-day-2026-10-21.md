# October 21 campaign: tests pass, hosted reporter blocked by budget

Scheduled run [39582029908](https://github.com/taskscape/PeerSync/actions/runs/39582029908),
workflow number 4/attempt 1, tests the unchanged pinned source
`3cf9eaf2cf200da4862484a1b5f2e180693657a0`. All five Windows test jobs and
the planner complete successfully. The reporting job never starts: GitHub
records runner ID 0, no steps, and the annotation:

> The job was not started because an Actions budget is preventing further use.

The overall workflow conclusion remains **failure**. There is no hosted
coordinator artifact or reporter log. No product job fails, is canceled,
skips scope or is rerun. Preserve this distinction rather than relabeling
the workflow green.

| Shard | Actual job ID | Passed methods | Job duration |
| --- | ---: | ---: | --- |
| 0 | 119161754992 | 13 | 32m39s |
| 1 | 119161755090 | 7 | 23m19s |
| 2 | 119161755332 | 7 | 13m53s |
| 3 | 119161754969 | 7 | 14m54s |
| 4 | 119161755189 | 7 | 20m34s |

Independent validation of all five actual uploaded artifacts proves 1000
distinct flat seeds across node counts 3, 4, 5, 6 and 7; five hierarchy
samples; all six retained regressions; and all 41 required successful TRX
method results. Five connected maintenance cases retire ten native archives
and evict ten blocks. Flat totals are 13858 operations, 9524 writes, 495
renames, 959 deletions and 9524 preserved ledger versions. Hierarchy and
retained results remain separate from those flat totals.

All manifests prove clean source, attempt 1 and the same actual Simulation
DLL SHA-256:
`C3AEE39F3B95B1C0A368AFE30C74A15B5D2B5530D56EC8D8D74F44C138C96ABB`.
Source/run/method/binary/maintenance metadata and actual case counts are
checked against authoritative job/step identities and completion times.
The last producer completes at 2026-10-21 10:38:32.284977 UTC; the workflow
finishes at 10:38:41 UTC on that same date. Its scheduled event was actually
created at 10:05:46 UTC, later than the configured 05:12 UTC trigger.

The daily validator from the exact pinned Git source independently validates
the downloaded artifacts. The current validator has identical daily logic;
only history cardinality changes under the
[approved two-day requirement](../requirements/P1-two-day-campaign.md).
The actual October 20 and October 21 reports pass that two-date artifact
history check. A single real October 20 report is rejected, and all thirty
planner/evidence/allocation controls pass, including insufficient/extra,
duplicate/gapped/mixed-source/rerun history rejection.

Evidence under `artifacts/p1-campaign-39582029908/` preserves all five full
shard artifacts and job logs, complete authoritative run/jobs/artifact
metadata, the zero-runner reporter job and its original annotations.
`revalidated-day.json` is locally reconstructed, not a hosted coordinator
artifact. `test-shards-authoritative-audit.json` explicitly records the
overall failure, reporter budget block and `qualifiedCampaignDay=false` at
the authoritative-acceptance layer. The separate artifact validator's
qualified result does not turn a failed GitHub run green.

The ceiling is reduced from 7 to 4, so these are the only two campaign
attempts. The tested source pin is unchanged; no budget extension or product
rerun is made. On 2026-10-21 the user explicitly
[accepts the reporting-only recovery](../proposals/P1-two-day-report-budget-recovery.md).
`approved-reporting-acceptance.json` rechecks current GitHub metadata, the
exact pinned verifier, all five successful hosted test jobs/41 methods and
the two-date history. It records the approval and accepted dates while
preserving the original failed workflow, runner ID 0/no steps and denial.
The earlier unapproved audit is retained unchanged.

Both October 20/21 dates are accepted under that narrow exception. Both
activation variables are removed and read back absent; the daily trigger
is removed in this change, retaining manual diagnostics only. The retirement
record is `artifacts/p1-two-day-campaign-retirement.json`. P1.6.4b/P1.6.4 and
Gate P1 remain open pending green merged PR/main validation of the two-day
tooling. The already accepted 100000-file corpus and every other gate remain
unchanged.

# PeerSync Actions usage audit - 2026-10-20

Read-only snapshot at 05:52 UTC of the private `taskscape/PeerSync` repository.
This is an estimate from workflow/job metadata, not an account invoice. Included
allowances, other repositories, tax, credits, deleted artifacts and cache/package
usage are not applied. Three jobs were unfinished in the fetched snapshot.

198 runs since 2026-10-18T14:57:13Z contain 163 CI runs and 35 Copilot review
runs. All run attempts were requested through the jobs API's `filter=all` view.
644 completed jobs report executed steps; 167 completed jobs report no executed
steps and are excluded from the runtime estimate. API fetches had zero errors.

| Measured activity | Executed jobs | Rounded runner minutes | Gross rate equivalent |
| --- | ---: | ---: | ---: |
| CI on standard Windows | 610 | 5076 | $50.76 |
| Copilot review on standard Linux | 34 | 224 | $1.34 |
| Total | 644 | 5300 | $52.10 |

Calculation uses per-job elapsed time rounded up to a whole minute, at currently
published standard rates of $0.010/Windows minute and $0.006/Linux minute.
See [GitHub runner pricing](https://docs.github.com/en/billing/reference/actions-runner-pricing).
All job labels in this snapshot map to those standard runner classes.

PR-triggered CI contributes 3154 minutes ($31.54 gross); push-triggered CI
1922 ($19.22). Early feature-branch pushes account for 690 of those push minutes;
the present workflow avoids duplicate feature-push/PR matrices. Failed/cancelled
jobs that actually started contribute 939 minutes ($9.39). Billing-denied or
otherwise unstarted jobs are not automatically counted as paid execution.
The largest completed run consumes 85 rounded runner minutes. The latest green
PR #38 run consumes 78 minutes across its five jobs. A workflow's wall time is
not the sum of the concurrent jobs, but runner usage adds each job's duration.

The snapshot retains 739 artifacts totalling 58.998 GiB. Of this, 169
self-contained P0 probe bundles account for 58.938 GiB; other evidence totals
about 0.060 GiB. Probe bundles already expire after seven days. The currently
listed artifacts represent about 1341.439 accrued GiB-hours, a gross October
storage-rate equivalent of roughly $0.45 at $0.25/GiB-month and 744 hours/month.
Keeping 59 GiB continuously for an entire month would be about $14.75 gross;
that is not an already accrued monthly charge. Allowances and deleted artifacts
can change the actual billed amount. See [GitHub billing and storage rules](https://docs.github.com/en/billing/concepts/product-billing/github-actions).

The evidence identifies frequent five-job Windows CI as the main runner-cost
driver. Current safeguards keep full PR/main/manual checks, avoid duplicate
feature-push matrices, batch related changes and preserve the five-job partition.
The five-day campaign remains inactive and requires an explicit five-attempt
bound. Its maximum failure ceilings correspond to 1375 Windows + 50 Linux
minutes ($14.05 gross at current rates), excluding the separate hosted preflight
and normal CI. This ceiling is not a prediction of actual usage.

No billing settings, schedules, workflow coverage or artifacts were changed by
this audit. Plan P0.1.4 explicitly requires published artifacts on every main/PR
run; removing PR bundle uploads would change that acceptance and is not silently
applied. The audit raw data is in `artifacts/actions-cost-audit/`: run metadata,
all fetched jobs, artifact metadata, measured-job rows and summary.json.

# P1 campaign: verified first day

[Run 39440958034](https://github.com/taskscape/PeerSync/actions/runs/39440958034)
passes its planner, all five Windows shards and coordinator on pinned source
`3cf9eaf2cf200da4862484a1b5f2e180693657a0`. Authoritative metadata confirms
workflow 390216948, number 3, attempt 1, workflow_dispatch and seven successful
completed jobs. The qualified producer completion date is **2026-10-20 UTC**;
GitHub's completed run is on that same date. This proves one day, not five.

Independent execution of the unchanged `build/campaign_evidence.py` exactly
matches the actual coordinator's report:

| Actual evidence | Result |
| --- | ---: |
| Distinct completed flat seeds | 1000 |
| Distinct generated hierarchy samples | 5 |
| Retained hierarchy regressions | 6 |
| Executed successful TRX methods | 41 |
| Actual node counts represented | 3, 4, 5, 6, 7 |
| Connected maintenance cases | 5 |
| Native archive retirements / block evictions | 10 / 10 |

Actual flat-case totals are 13866 operations, 9604 writes, 545 renames, 970
deletions and 9604 preserved ledger versions. Hierarchy samples and retained
regressions have separate case results; those numbers are not added to the
flat totals or mislabeled as 1000 hierarchical cases. All cases retain the
unchanged 24-round healing and native/reference/queue/version-preservation
oracles.

The leader executes thirteen methods: seven baseline methods and all six
retained seeds (794669444, 1044729930, 1583987140, 1608819303, 1648781653,
1878823561). Each other shard executes all seven baseline methods. Actual TRX
names, counts and outcomes are checked, not inferred from manifests. All five
manifests identify clean checkouts and the same actual Simulation binary:
`C3AEE39F3B95B1C0A368AFE30C74A15B5D2B5530D56EC8D8D74F44C138C96ABB`.
Maintenance proof matches each shard's source and binary.

| Shard | Job ID | Job duration including setup/upload |
| --- | ---: | --- |
| 0 | 118690494368 | 39m59s |
| 1 | 118690494410 | 31m11s |
| 2 | 118690494623 | 40m17s |
| 3 | 118690495066 | 21m14s |
| 4 | 118690494296 | 21m56s |

The existing 75-minute leader and 50-minute other-shard ceilings are unchanged.
Only one campaign attempt is executed; there is no product rerun or budget
extension. Preserved artifacts include each complete shard, its job logs,
coordinator, authoritative run/jobs metadata, `revalidated-day.json` and
`authoritative-day-audit.json` under `artifacts/p1-campaign-39440958034/`.
Earlier partial audits remain explicitly non-qualifying.

The complete report has `qualifiedCampaignDay=true` and `gateP1Passed=false`.
Only the verified date 2026-10-20 is accepted. The source pin remains unchanged
and the last permitted workflow number is 7, leaving four bounded attempts
(4 through 7) under the existing daily 05:12 UTC schedule. Future dates and
their results are unverified. Do not manually consume another attempt,
dispatch a preflight, rerun a day, or auto-extend the ceiling.

The immutable repaired-source full corpus remains active. P1.6.4 and Gate P1
stay open until five actual consecutive UTC completion dates, authoritative
GitHub conclusions, full artifact/history checks and all other P1 criteria
are verified. No P2 work starts from this first successful day.

# Bounded P1 CI campaign

This workflow is inactive until deliberately configured. Normal PR/main CI
keeps its five jobs and all default tests; its existing boundary job also runs
the campaign planner/evidence control tests before starting the long checks. Campaigns are separate evidence
for Gate P1; they never close the phase by themselves.

Each campaign day has five Windows shards, each running 200 flat seeds and
one generated hierarchy sample. Shard zero also runs the four retained
hierarchy regressions; the other shards omit those duplicate cases only.
Every shard keeps the existing native/preservation/reference/queue oracles
and six baseline Simulation methods. Total: 1000 distinct flat seeds, five
hierarchy samples and four fixed regressions. These counts do not mean
1000 hierarchical cases. Shard zero has a 75-minute failure ceiling; others
have 50 minutes. Five attempts are the activation maximum. Timings and counts
remain observable in uploaded artifacts.

Before activation, merge the campaign implementation, require green PR/main
CI, then manually dispatch **P1 bounded seed campaign** in `preflight` mode.
It runs ten flat/five hierarchy samples, omits the four long regressions,
and produces `qualifiedCampaignDay=false`. It cannot satisfy the daily gate.
Review every shard, its executed TRX methods and the coordinator artifact.

Activation requires one immutable, merged source SHA whose five normal CI
checks are all green. Set repository variables `PEERSYNC_P1_SOAK_SOURCE` to
that full SHA and `PEERSYNC_P1_SOAK_LAST_RUN` to the next campaign workflow
run number plus four. Calculate the next number from the actual latest
workflow record, after preflight; do not guess it from another workflow.
Dispatch the first `campaign` run, then the daily 05:12 UTC trigger supplies
the remaining attempts. No activation variable is set by this implementation.

The scheduled job condition allocates no runner without both activation
variables or after the permitted workflow run number. Planning rejects
non-merged/non-green sources, more than five remaining attempts, source
overrides and rerun attempts. Manual preflights consume workflow run numbers,
so do not insert them into an active campaign. Failed/missed days remain
failed/missed; the workflow never extends its own budget. Clear both variables
to stop future scheduled work, and retire the schedule after acceptance.

Each `p1-soak-shard-<number>` artifact includes case lists/summaries, actual
TRX, source/run/attempt/mode metadata, executable hash and retained failures.
`p1-campaign-day` includes the coordinator's report. Missing, cancelled,
mixed-source, duplicate-seed/shard and incomplete-method evidence fails.
Generated manifests used in validator unit tests are controls, not CI days.

For final history, download five actual `p1-campaign-day` artifacts from
distinct successful run IDs. Check each authoritative GitHub run and attempt
conclusion, event, workflow, source pin and coordinator/shard results; require
the same pinned source and consecutive UTC completion dates. Then audit the
five saved reports:

```powershell
python build/campaign_evidence.py --source <full-source-SHA> `
  --history <day1.json> <day2.json> <day3.json> <day4.json> <day5.json> `
  --output artifacts/soak-history.json
```

The artifact-only audit keeps `gateP1Passed=false` and explicitly requires
the live GitHub check. A status badge, preflight, local 1000-case run or five
attempts on one calendar date cannot prove five campaign days. Keep the
100000-file local corpus, required main CI and approved external validation
gates separate. No checkbox is ticked before actual evidence and merged CI.

# Convergence, native runner and hosted allocation acceptance

[PR #41](https://github.com/taskscape/PeerSync/pull/41) is squash-merged at
`3cf9eaf2cf200da4862484a1b5f2e180693657a0` on 2026-10-20 at 13:43:40 UTC.
Its exact tree is `8d0e12fe26e61d2b17fd404ca3b360e17801c855`, identical to
the tested synthetic merge `9aa04d561ca0a1c55f7a86f4a3fed2242eb9789c` and
submitted head `60ea0f02e6e393cd7f22f1fa3b7ef7310f2518e1`. Fresh review-thread
inspection finds no threads or pagination. No reviewer messages were sent.

All five required jobs pass in both
[PR run 39428557600](https://github.com/taskscape/PeerSync/actions/runs/39428557600)
and [main run 39432915856](https://github.com/taskscape/PeerSync/actions/runs/39432915856).
Actual downloaded environment manifests bind every job to its real source:
the synthetic merge in PR CI, and the squash commit in main CI.

| Actual evidence | PR | Main |
| --- | ---: | ---: |
| Distinct unit results: 1013 Engine plus 552 other units | 1565 passed | 1565 passed |
| Additional Server 2025 Engine results | 1012 passed | 1012 passed |
| Simulation methods / retained seeds / maintenance cases | 13 / 6 / 1 passed | 13 / 6 / 1 passed |
| Generated flat / hierarchy samples | 8 / 4 passed | 8 / 4 passed |
| Actual fuzz seeds | 115323 | 100992 |
| Actual fuzz seconds, all four targets, zero crashes | 600.0062657 | 600.0068327 |
| Fuzz supervisor seconds, separate from actual fuzz | 1560.4307924 | 2326.5165657 |
| Planner/evidence/allocation controls | 28 passed | 28 passed |

The Server 2025 method set is exactly the Server 2022 set minus the existing
duplicate generic startup-capacity stress method. Its results are additional
platform execution, not another 1012 distinct unit tests. Root x64 TRX files
are counted once; the retention child TRX is not counted twice. Actual test
names and counters, rather than only totals, are compared against the PR scope.
All ten new concurrent-capture controls and all 24 directory/author/root-proof
controls are present in both Server suites. The new Storage catalog predicate
is included in its 300-test project scope.

Both Servers pass virtual-account execution, denied private-key access,
unauthorized-pipe rejection, authorized caller-SID verification and all native
probes. Main Server 2022 records 78.3964421 build seconds and 672.0082307 total
seconds; Server 2025 records 101.4162407 build seconds and 618.067018 total
seconds. Both are within the unchanged 780-second script budget and 15-minute
job ceiling. This completes P1.6.5 after its preserved timeout, local baseline,
focused build-closure and exhausted-budget controls. No native scope is removed.

The x64 job passes its 1 GiB transfer and 100000 watcher operations. Both build
jobs pass temporary-identity signing, timestamp verification, tamper detection
and certificate/trust cleanup without exporting the private key. ARM64 is
compile/publish and signing verification, with runtime checks explicitly false.
It is not ARM64 runtime or clean-machine deployment evidence.

Strict Simulation scope validation confirms all six permanent regressions,
including 1608819303 and 794669444. Each actual retained result has its expected
seed and remains within the unchanged 24-round healing limit. Connected
maintenance actually retires two archives and evicts two blocks while both
versions survive. The prior local reproduction/repair/negative controls remain
in the [directory feedback report](P1-hierarchy-publication-feedback-2026-10-20.md)
and [subtree revival report](P1-subtree-revival-2026-10-20.md). Together with
merged current PR/main proof, this completes P1.6.2d and P1.6.2e.

Evidence is preserved under `artifacts/ci41-capture-*` and
`artifacts/ci41-main/`, including actual TRX, source environments, signing,
security, budget, simulation and raw fuzz results. Independent exact-scope
audits are `artifacts/ci41-capture-combined-audit.json` and
`artifacts/ci41-main-audit.json`; raw boundary job logs also show all 28 controls.

## Actual cached-planner allocation proof

Only after main is fully green, exactly two manual Linux diagnostics run on
the same merged source. Each first attempt has a successful planner, the sole
intentional shard failure, and a successful diagnostic report. Its authoritative
attempt metadata and logs are saved before exactly one failed-job-only rerun.

| Mode / run | Original planner job | Cached planner record | Attempt-two result | New attempt-two runners |
| --- | ---: | ---: | --- | ---: |
| [campaign 39439185027](https://github.com/taskscape/PeerSync/actions/runs/39439185027) | 118684211366 | 118684802036 | skipped | 0 |
| [preflight 39439200419](https://github.com/taskscape/PeerSync/actions/runs/39439200419) | 118684269427 | 118684906321 | success | 2 |

GitHub assigns a new API job record and `run_attempt=2` to each cached
successful planner. The planner's original runner ID, start/end timestamps and
every step/timestamp are unchanged. Its actual log still reports
`plannerAttempt=1`. Preserve this original-to-cached record mapping; do not
claim the API IDs or API attempt fields remain identical.

In campaign attempt two both downstream records are skipped, with null runner
IDs and empty steps. The reused planner has no new execution. Therefore no
fresh runner is allocated. Preflight attempt two actually executes both
downstream jobs successfully, and both logs prove `cachedPlannerAttempt=1`.
Exactly eight Linux runners execute across the complete diagnostic: six on
the two first attempts and two on the positive rerun. No Windows runner,
product test, campaign workflow counter or activation variable is used.
Every diagnostic reports `qualifiedCampaignDay=false` and `gateP1Passed=false`.

Authoritative per-attempt metadata/logs are preserved under
`artifacts/workflow-attempt-captures/{39439185027,39439200419}/attempt-{1,2}/`.
The negative second attempt needs metadata, not nonexistent fresh job logs.
`artifacts/p1-hosted-allocation-probes-audit.json` independently checks the
original execution mapping, actual allocation counts, first-attempt negative
scope and positive cached outputs. This completes P1.6.4a and P1.6.4a1, without
qualifying a campaign day or changing any gate.

## Remaining acceptance

The repaired-source 100000-file corpus remains active on immutable binaries
from `dd7059b43b25ff18614d7145211e8851c668a23a`. Its product/build/test/workflow
inputs are verified unchanged through merged main, but binary informational
versions and evidence retain their actual revision. Clean 32/4096-file checks
pass; they do not replace the full corpus. The original failing fixture remains
untouched. P1.2.4 and its reopened ownership descendants stay open until the
mandatory actual repaired-source corpus evidence is complete.

The earlier timestamp-repair source completes its own
[100000-file corpus](P1-scale-timestamp-100000-2026-10-20.md), with all 100024
versions preserved and independent source/binary/cleanup checks. It retains its
actual earlier revision and does not replace the running repaired-source case.

The new bounded hosted preflight,
[39439657652](https://github.com/taskscape/PeerSync/actions/runs/39439657652),
passes all seven jobs on the merged source, workflow number 2, attempt 1.
Actual clean manifests and seven successful baseline methods per shard prove
35 method results, ten distinct flat seeds, five hierarchy samples and five
maintenance cases with ten archive retirements and ten block evictions. The
flat samples contain 152 operations, 101 writes, four renames, eight deletions
and 101 versions; sampled node counts are 3, 4, 6 and 7. Preflight intentionally
omits the six retained cases and does not qualify as a campaign day.

All five actual Simulation binaries have SHA-256
`C3AEE39F3B95B1C0A368AFE30C74A15B5D2B5530D56EC8D8D74F44C138C96ABB`.
The coordinator report exactly matches independent local validation through
`build/campaign_evidence.py`. Actual metadata, manifests, TRX and coordinator
are under `artifacts/p1-hosted-preflight-39439657652/`; separate evidence is
`artifacts/p1-hosted-preflight-revalidated-39439657652.json` and
`artifacts/p1-hosted-preflight-audit-39439657652.json`.

Only after these checks, activation reads back the actual latest workflow
counter, 2. `PEERSYNC_P1_SOAK_SOURCE` is pinned to the full merged SHA above and
`PEERSYNC_P1_SOAK_LAST_RUN` is set to 7, permitting exactly five attempts,
numbers 3 through 7. Both values are read back from GitHub. The first campaign,
[39440958034](https://github.com/taskscape/PeerSync/actions/runs/39440958034),
is dispatched once as workflow number 3, attempt 1. The daily 05:12 UTC trigger
supplies later attempts. Do not dispatch extra preflights or rerun campaign
attempts, extend the ceiling, or treat dispatch as a completed day.

The first campaign outcome and five qualifying consecutive UTC completion
dates remain unverified. Actual authoritative run/attempt conclusions and
artifact/history checks are mandatory; the preflight and Linux diagnostics
remain non-qualifying. The complete Gate P1 stays open. P2 has not started.

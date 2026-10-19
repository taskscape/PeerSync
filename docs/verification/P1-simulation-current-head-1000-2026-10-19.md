# Submitted-head 1,000-case local simulation

`build/Run-Simulation.ps1 -Cases 1000 -NoBuild` passes against the verified
ordinary build outputs of PR #35 head
`9f17d76f335e2b7940d060b5fcba2a4634e7cb0c`. The process exits 0. All three
xUnit methods pass: the generated batch, saved checker regression and native
loss-oracle negative control. The runner reports 12m42.229s; generated sampling
reports 759.6139745 seconds.

| Observation | Result |
| --- | ---: |
| Completed scenarios / distinct seeds | 1,000 / 1,000 |
| Real NTFS/SQLite nodes per scenario | 3–7 |
| Proposed operations | 13,974 |
| Published payload ledger versions / writes | 9,696 / 9,696 |
| Actual renames | 508 |
| Actual deletions | 940 |
| Internal parked staging files observed after healing | 284 |
| Maximum healing rounds | 2 |

The existing oracle requires every published payload in current/conflict or
accounted archive bytes; private blocks and internal temporaries alone do not
satisfy preservation. Final visible trees agree, needed work drains, recovery
intents are empty and high-water marks remain stable. The negative control
separately proves that private cache alone fails the loss assertion.

The run covers the existing flat-path randomized write/rename/delete and
directional transport delay/drop/partition scenarios. It includes the latest
connected-session fixes and hierarchy/staging implementation, unlike the older
1,000-case batch on PR #34's first head. It does not add hierarchical/case/crash
generation or force 24-hour staging expiry in these scenarios; focused native
tests cover that expiry separately. This is local evidence, not 1,000 CI cases,
five consecutive CI days, the five-node 100,000-file corpus or Gate P1 passage.

Case-clash feature probes were developed on the subsequent local branch using
`--artifacts-path artifacts/case-baseline`, leaving this run's ordinary build
outputs unchanged. Their four known feature failures are not part of the
submitted head or this simulation batch. No production source/build-script
changes were made after the tested submitted head during the batch.

Evidence: `artifacts/p1-staging-current-1000.log`,
`artifacts/simulation/20261019T154022-501bb3afb9c2417d93654193fd693c56/`
(`cases.jsonl`, `summary.json`) and
`artifacts/tests/simulation/PeerSync.Simulation_net10.0-windows10.0.20348.0_x64.trx`.
PR #35 CI run 39279516947 remains queued with no started steps/annotations at
15:53 UTC; no rerun or duplicate feature-push run was requested.

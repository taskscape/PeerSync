# Bounded ordinary CI Simulation cohorts: local verification

PR #42's first normal run [39588878721](https://github.com/taskscape/PeerSync/actions/runs/39588878721)
tested submitted source f594297a44d78b6f55a1a68fdc3e8a9fbb6146eb through
synthetic checkout 56862fa3225c2edbe00c5554b33d87e886430663. All four packaging
and Server jobs passed. Boundary verification exceeded the unchanged 50-minute
job ceiling. Its actual TRX preserves eleven passing methods, no failed
assertions, and incomplete retained/maintenance coverage. This is not a pass.

Four generated hierarchy cases took 21m15s. Five retained seeds took another
22m09s. Seed 1648781653 only started near the deadline and was canceled.
Its unchanged submitted-source replay passes in 55.603027 seconds, with the
actual binary hash unchanged. The cancellation stack alone does not establish
a production defect. Original artifacts/logs/annotations remain under
`artifacts/ci42-pr/`; focused replay/source proof is under
`artifacts/p1-ci42-retained164-current/`. An initial stale frozen-worktree
Simulation executable was rejected before execution and was not rebuilt.

The [runner design](../proposals/P1-bounded-ci-simulation-cohorts.md) was
committed with the source design/decision/P1.6.4c WIP before implementation.
The existing job now owns concurrent baseline and retained cohorts, plus the
unchanged fuzz worker. Scope, five jobs and 50-minute job ceiling are retained;
all build/execution work shares a 2700-second script budget. Default local
and campaign Run-Simulation behavior is unchanged.

Actual local full verification uses committed source
`1b5c49a6bfd1e937e8f046c3206b8f8db9e161b1` and the ordinary command
`./build/Run-BoundarySimulation.ps1 -FuzzSeconds 600`:

| Check | Actual result |
| --- | --- |
| Locked restore/build | Pass, zero warnings/errors; pre-worker time 12.2686208 s |
| Aggregate actual TRX | 13 unique passed methods: 7 baseline + 6 retained |
| Generated scenarios | 8 flat and 4 hierarchy cases, complete distinct case lists |
| Retained seeds | All 6 passed, within unchanged 24-round bound |
| Connected maintenance | Pass; 2 native archives retired, 2 blocks deleted, both versions preserved |
| Four-target fuzz | 220672 seeds, 600.0010403 s, zero crashes |
| Actual copied assemblies | All 11 SHA-256 hashes unchanged during execution |
| Owned workers | All 3 exited 0; no cleanup errors |
| Total build/execution | 612.4884169 s, within unchanged 2700 s budget |

Native convergence, reference coverage, drained queues and published-version
survival assertions are unchanged. Source comparison confirms production,
Fuzz and Simulation inputs are identical to the accepted campaign source
3cf9eaf2cf200da4862484a1b5f2e180693657a0. This runner-only change does not
require new thousand-seed dates or a repeat of the completed full corpus.

Twelve scope controls accept complete single/two-report coverage and reject
missing/duplicate/omitted/failed/skipped/wrong/extra/empty/partial/DTD evidence.
Two actual supervisor controls reject a pre-start expired budget and stop all
owned siblings after a failed worker while preserving an unrelated process.
All thirty existing campaign controls pass. Syntax/whitespace checks pass.

Original local outputs are `artifacts/p1-ci42-cohorts-local.log`,
`artifacts/simulation/20261021T121012-3e6478bdb1334981bf597d3feb5e997b/`,
the matching cohort TRX directories, `artifacts/p1-ci42-cohorts-fuzz-proof/`,
and independent scope/full audit JSON files. Actual new-source PR/main CI
and artifact audits remain pending. This report does not close P1.

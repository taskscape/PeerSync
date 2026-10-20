# Local multi-node simulations

Run from the repository root in PowerShell on supported Windows. The tests
use generated isolated NTFS roots, real indexes and the public engine over the
in-memory transport. They do not validate physical LAN, native NAT, deployed
services or production TLS authentication.

```powershell
.\build\Run-Simulation.ps1 -Cases 8 -HierarchicalCases 4
```

`Cases` selects the existing flat-path mode (default 1000). `HierarchicalCases`
selects the separate nested-directory mode (default 4). Both are required by
the runner and have separate counters and summary files. `-NoBuild` requires
fresh Release binaries. The canonical local verifier requests eight flat
cases plus the default four hierarchical cases. No new scheduled CI job is
created by this harness. A 1000-case expanded CI configuration and five-day
acceptance remain open; measure runtime before selecting shards and budgets.

Hierarchy scenarios create three to seven actual engine nodes. They generate
nested writes, empty directories, file/directory renames, subtree deletion,
file/directory replacements and independent case spellings while links drop,
partition and heal. The trace records skipped as well as actual mutations.
Each healed round advances injected retry clocks. Temporary pass failures may
retry; success requires matching exact visible tree spellings and bytes,
stable signed index high waters, no needed/apply/case/publication work, complete
authenticated reference coverage and every published generated payload in
visible current/conflict files or an indexed native archive. Private caches,
unindexed internal files and reparse targets cannot satisfy preservation.

The runner prints its evidence directory under `artifacts/simulation/`.
`summary.json` and `cases.jsonl` describe flat cases;
`hierarchical-summary.json` and `hierarchical-cases.jsonl` describe hierarchy.
`gateP1Passed` stays false. The staging count is a legacy root-level diagnostic,
not proof that all nested/private temporary entries were retired.

Every failure retains a generated fixture, JSONL diagnostics, SQLite indexes,
operation trace, seed/mode and replay command, and attempts a fixture ZIP.
`latest-failure.txt` points to the most recent `replay.json`; earlier failures
remain. These are test data only. Never point mutation helpers at user roots.
Promote discovered defects to focused regressions before accepting a repair.

Replay one retained hierarchy seed without sampling/shrinking:

```powershell
$env:PEERSYNC_SIMULATION_HIERARCHY_CASE = '1878823561'
$env:PEERSYNC_SIMULATION_ARTIFACTS = Join-Path $PWD 'artifacts/simulation/replay'
dotnet test --project tests/PeerSync.Simulation -c Release --filter-method '*GeneratedHierarchical*'
Remove-Item Env:PEERSYNC_SIMULATION_HIERARCHY_CASE
Remove-Item Env:PEERSYNC_SIMULATION_ARTIFACTS
```

The flat replay uses `PEERSYNC_SIMULATION_CASE` and `'*GeneratedFlat*'`.
Seeds 1878823561, 1648781653 and 1044729930 are also checked-in theory cases
under `RetainedHierarchicalRegressions*`; the regular runner executes them
separately from the requested random case counts. Their evidence is written
beneath `regressions/<seed>/`. They exercise the complete native convergence
and version-preservation oracle, and therefore take longer than focused tests.
Hierarchy test folder keys derive only from the public synthetic seed; they
are never suitable for deployed folders. Normal production keys remain random.
The batch runner clears direct replay overrides while running and restores
all prior simulation environment variables afterward.

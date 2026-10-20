# Explicit five-node corpus validation

Run from an elevated Windows development checkout with .NET 10. This is local
P1 validation through the public in-memory peer transport, real NTFS and
SQLite. It does not test a deployed service, physical LAN or native NAT.

Start with native oracle controls and a small five-node run:

```powershell
.\build\Run-Scale.ps1 -Files 32 -OracleOnly
.\build\Run-Scale.ps1 -Files 32 -Seconds 300
.\build\Run-Scale.ps1 -Files 512 -Seconds 900
```

Measure intermediate scale before the full corpus:

```powershell
.\build\Run-Scale.ps1 -Files 4096 -Seconds 1800
.\build\Run-Scale.ps1 -Files 100000 -Seconds 43200
```

The deadline includes generation, initial publication, complete five-peer
replication, partitioned conflicting overwrites/renames/deletions, healing,
reference coverage and native version preservation. The runner uses bounded
index pages and engine passes. Injected retry clocks advance only when work
is waiting; the runner does not sleep to bypass retry policy.

Every run creates a fresh fixture beneath the Windows temporary directory;
it never accepts an existing user corpus as a mutation target. Failures retain
that fixture and its five databases/log directories. Success removes it after
checking the exact generated path, normal root and ownership marker. Native
oracle controls reject missing nested bytes, private cache-only copies and
reparse aliases. No failure ZIP is automatically created for a large corpus.

`SCALE_EVIDENCE` identifies the unique report directory beneath
`artifacts/scale/`. Inspect `summary.json`, `source-state.json`,
`progress.jsonl`, `rounds.jsonl`, `initial-native.json` and `final-native.json`.
Progress is emitted every 30 seconds. Reports retain actual file/version and
mutation counts, phase timing, native file counts, final queue/high-water
state, runtime/hardware and executable version/hash provenance. A missing
summary is an execution/build failure, never a passing scale result.

`-NoBuild` requires an executable built from the current Git revision. Commit
source changes before authoritative runs and inspect `workingTreeDirty` in
`source-state.json`; a revision label does not make a dirty checkout clean.
The final loss oracle requires every original and newly generated payload in
visible current/conflict files or indexed native archives across the five
peers. Private blocks cannot satisfy it. Initial native count must equal the
requested corpus; final count changes after explicit deletions are reported
separately.

Small/intermediate passes have `corpusRequirementSatisfied=false`. A successful
100000-file run supports only the corpus line of Gate P1. `gateP1Passed` remains
false until every phase criterion, including merged CI and the separate
1000-seed/five-day requirement, has been satisfied.

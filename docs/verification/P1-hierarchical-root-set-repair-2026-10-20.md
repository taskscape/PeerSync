# Expanded hierarchy and retained regressions: local repair evidence

The original 20-case batch failed on route repair source 1154511. The two
retained clock failures, and the earlier missing-route failure, now pass after
schema-35 parent root proof and the bounded payload routing repair. This
report supersedes the current-status implication of the historical routing
and clock baseline reports; their original measurements remain useful.

The property worktree built source b4dff118f372372a96a3bb9f152685c06592c94f,
including production root-proof source 331310d. Rebasing that worktree and
integrating it into the directory PR changed documentation/history, not the
tested src/tests contents. The harness and retained regression theory cases
are now included with the production repair for the next single CI batch.

A fresh requested batch passes all 20 distinct hierarchy seeds on three to
seven actual TransportFolder nodes, with real Windows files and SQLite. It
records 282 operations, 254 published payload versions, 21 directory renames,
49 subtree deletions and 23 kind changes. The operation count includes
generated attempts; mutation counters record completed changes, and traces
retain skips. All cases pass the recursive exact
native tree, stable index/drained queue, authenticated reference coverage and
visible/indexed-native-archive preservation oracles. Private caches cannot
satisfy the version oracle. No failure artifact was produced.

Batch summary: completed 20, status passed, 362.7365255 seconds, MTP exit 0 in
363.467 seconds. Evidence in the property worktree:

- `artifacts/p1-hierarchical-20-root-set.log`
- `artifacts/simulation/hierarchical-20-root-set/hierarchical-summary.json`
- `artifacts/simulation/hierarchical-20-root-set/hierarchical-cases.jsonl`

The checked-in theory separately passes all three retained seeds 1878823561,
1648781653 and 1044729930, MTP exit 0 in 181.179 seconds, no skips. Each case
uses the full hierarchy/native/preservation oracle. Evidence:
`artifacts/p1-retained-hierarchy-root-set.log` and
`artifacts/simulation/retained-hierarchy-root-set/regressions/<seed>/result.json`.
The earlier direct replay measurements are in
`P1-directory-parent-root-set-2026-10-20.md`. Compiler/configured analyzer
checks for Simulation are clean at warning/error, with no workspace warning
or truncation.

The previous expanded failure remains retained; a successful new random batch
is not a bit-identical replay of that generator stream. The three permanent
cases retain its discovered defects. Native timestamps and scheduling can
change the number of healing rounds and derived alias names.

The integrated canonical runner passes on source d5e7a0e, exit 0 in
454.1938069 seconds: all 1534 unit cases (983 Engine, 299 Storage) and all
nine Simulation cases pass with no skips. The runner verifies the requested
eight flat and four hierarchy samples in addition to the three fixed
regressions. Diagnostic/control/native/transport probes, the 100000-operation
watcher check and win-x64 publishing pass. Boundary fuzz is explicitly skipped
locally and remains required in CI. Evidence in the primary checkout:

- `artifacts/p1-hierarchy-integrated-canonical.log`
- `artifacts/verification-win-x64.json`
- `artifacts/tests/` and `artifacts/tests/simulation/`
- `artifacts/simulation/20261020T024826-2f5d13f63b2f48b2bcadfd7ada452504/`
- `artifacts/p0/summary.json`

Final-head CI/main remain pending here.
The existing packaging job now has a 20-minute failure ceiling for the added
bounded simulation scope. Job count and schedules are unchanged. All P1
1000-seed/five-day/100000-file, installed service, authenticated production
transport and deferred external LAN/NAT acceptance remain unverified.

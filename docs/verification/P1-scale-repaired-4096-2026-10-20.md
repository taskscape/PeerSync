# Repaired-source five-node intermediate scale

Command: `build/Run-Scale.ps1 -Files 4096 -Seconds 3600` in a clean isolated
worktree at `aada02d095b4b13c165850c59f433e96aa33607a`. A fresh Release build
preceded execution; the report's four binary informational versions match
the full source revision and retain SHA-256 hashes. The runner exits 0 after
780.437234 seconds on .NET 10.0.13, Windows build 26300, x64/12 processors.

All five public TransportFolder nodes initially converge on exactly 4096
native files and 32 directories. Partitioned mutations perform 24 conflicting
overwrites, four renames and four deletions. After healing, every native tree
is identical, with 4092 visible files and 32 directories; all 4120 original
and newly published payload versions survive in current/conflict files or
indexed native archives. Private caches do not satisfy the loss oracle.

All five final states have zero needed rows and no apply, case or publication
work/retry deadline. Fresh authenticated reference coverage passes before the
native oracle. Per-peer local high-water sequences differ as expected and are
not substituted for native tree equality. The run takes 29 convergence rounds.
Initial publication takes 53.176 seconds, initial convergence 564.125,
partitioned mutations 20.925 and healed convergence 137.838 seconds.

Actual summary, source-state/progress and initial/final native reports are in
`artifacts/scale-final-4096-aada02d/`; the terminal build/run log is
`artifacts/p1-scale-final-4096-aada02d.log`. `workingTreeDirty=false`, actual
runner exit is 0, and the owned fixture's removal was independently confirmed.
The separate original 100000-file run continues on its immutable older source.

This is intermediate-scale evidence on the repaired submitted source. Both
`corpusRequirementSatisfied` and `gateP1Passed` remain false. A completed
final-source five-node 100000-file run, five actual consecutive campaign days,
required CI/main and the remaining P1 criteria are still mandatory.

# Timestamp repair: current-source scale checks

Both checks use clean, immutable repair source
`fa68cdc58172bc2a6717e175520c96edc435454a` in the isolated scale worktree.
The runner binds Benchmark/Engine/Storage/Crypto informational versions and
SHA-256 hashes to that source. It uses five public TransportFolder engines,
real NTFS/SQLite, production in-memory peer exchange and unchanged native,
archive/version, reference and durable-queue oracles.

The fresh 32-file preflight passes in 20.2625593 seconds and six rounds.
It performs sixteen overwrites, four renames and four deletions, then finds
28 final files and all 48 original/new versions. All final queues drain,
native/reference checks pass and the owned fixture is removed.

The 4096-file intermediate run passes in 861.2568627 seconds and 29 rounds.
All five initial native trees contain 4096 regular files/32 directories.
After partitioned 24 overwrites, four renames and four deletions, the final
trees agree at 4092 regular files/32 directories. All 4120 expected payload
versions survive in native current/conflict bytes or indexed archives.
All needed, apply, case and publication work drains; complete references
pass before native verification. Actual source-state records a clean checkout
and runnerExitCode=0; the summary records fixtureRemoved=true.

The completed intermediate evidence is copied to
`artifacts/scale-timestamp-4096-fa68cdc/`; its original directory is
`artifacts/scale/20261020T084413-706d817e00e348cb83bf60bb928df3d5/`
in the isolated worktree. The 32-file evidence remains in
`artifacts/scale/20261020T084351-5f5946a3a1ce4d0fa0fee77cd3b5b262/`.

Only after both checks pass does the same pipeline start its explicit
100000-file run with those unchanged binaries and a twelve-hour deadline.
The full run is active and unverified. Smaller reports retain
corpusRequirementSatisfied=false/gateP1Passed=false. No full corpus or P1
checkbox is ticked from these intermediate results.

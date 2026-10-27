# Three-hour soak evidence tooling control, 2026-10-26

The revised three-hour capture and comparison tools passed a focused Windows
PowerShell 5.1 control. This validates report wiring and rejection behavior;
it does **not** constitute a physical or elapsed-time soak.

`build/Test-PeerSyncThreeHourSoakEvidence.ps1` ran with exit code 0. It first
invoked the packaged capture-loop source against one actual local file with
`Intervals 0`. The capture exited 0 and produced a complete, one-file snapshot
using the current tree collector SHA-256
`94B81351E1632B748DC3E0D4F08F6D682B3558C2754E7CFA359E8A1544025588`.

The control then generated explicitly **synthetic** hourly snapshots for three
machine labels at four consecutive UTC hours. The current batch comparator
exited 0: four requested and four matched hours, with `auditPass: true` and no
issues. A separate fixture omitted one peer's third-hour snapshot. Its batch
exited 1: three of four hours matched, `auditPass: false`, and the failed hour
was reported with `hourly-report-invalid`, `insufficient-hours` and
`missing-or-duplicate-hour` issues. The positive and negative fixtures use
invented digests and timestamps; they are never evidence of actual content or
three hours of execution.

The retained result, live snapshot and both synthetic comparison summaries
are under `artifacts/field/soak-three-hour-control-20261026T115308Z/` (ignored
by Git). The top-level `control-result.json` records the exact three tool
hashes, output paths and exit codes. The test script refuses an existing output
directory so a rerun cannot overwrite that evidence.

Gate P2 still requires a 9 GiB physical three-PC convergence run and the
three-hour LAN operation soak with four real matching hourly comparisons.
Gate P4 needs its own physical WAN soak, and Gate P5 needs release-candidate
LAN and WAN soaks. This control closes only the revised-tooling check.

## Long-path regression, 2026-10-27

The third physical PC exposed a Windows PowerShell 5.1 `MAX_PATH` failure in
the original tree collector. The focused control now creates 24 deep levels
and a file past 260 characters, then checks both the live content-budget
walker and real capture. The Windows PowerShell 5.1 run exited 0 with two
files, 24 directories, the expected bytes and corrected collector SHA-256
`5C1E27BC7E9E75DA29D062C4D11346D9C294BB7963D9B30337609108D1BDED53`.
The result is retained at
`artifacts/field/physical-50k-20261027/soak-control-longpath-budget-20261027/control-result.json`.
This is a tooling regression check, not a new physical-soak or timed-corpus
pass.

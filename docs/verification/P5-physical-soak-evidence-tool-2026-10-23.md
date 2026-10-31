# Physical soak evidence tool, 2026-10-23

`build/Measure-PeerSyncTree.ps1` now provides three read-only modes: capture
the full native tree (excluding only root `.psp`) as a framed SHA-256 digest,
compare same-hour peer snapshots, and audit a consecutive hourly sequence.
Capture rejects reparse points, a changed file/entry set during the scan,
and an output placed inside the synced root. The JSON reports carry counts,
digests, UTC timestamps and bounded issue codes; they do not carry native
paths, file names, contents, keys or network addresses. The physical operator
procedure is [documented here](../physical-soak.md).

The final script SHA-256 is
`78027326BBEF5E4A2200D553D8F80E517F03F2B3852B3378DFA873EB19C37F3A`.
That exact source was copied to the installed Server 2022 and Server 2025
guests and run with Windows PowerShell 5.1 against their previously
synchronized target/source folders. Both final captures exited 0. After the
scoped operation control, each measured seven files, eleven non-metadata
directories and 2,046,346 bytes, with matching
tree SHA-256
`B8EBE308F65097B1BBAB0FC791F890B7B48DEE036754321390665BA2326BCB68`.
The host comparison exited 0 with `match: true`, no issues, a common run ID
and capture starts about one second apart. The final JSON evidence is under
`artifacts/vm/soak-capture-loop-final-2026-10-23/`.

A local changed-file fixture produced two valid captures but comparison
exited 1 with `tree-mismatch`. A separately marked **synthetic** 74-report
sequence passed the 72-hour capture-time audit when compared after the
reported capture period; omitting hour 37 exited 1 with
`hourly-report-invalid` and `missing-or-duplicate-hour`. The Windows
PowerShell 5.1 batch comparison and audit used process-scoped
`-ExecutionPolicy Bypass`, since that host blocks unsigned scripts by
default. See [capture automation evidence](P5-physical-soak-capture-automation-2026-10-23.md).
These controls verify decisions, not an elapsed physical soak.

The tracked `build/Build-PeerSyncFieldKit.ps1` packages the exact signed
development installers, the public signing certificate, the field collector,
the tree tool and operator guides. It validates the installer hashes,
source revision, matching public certificate and Authenticode signatures.
The rebuilt local ZIP passed `python -m zipfile -t` and is recorded in
[field-collector evidence](P4-field-deployment-collector-2026-10-23.md).

This two-guest, single-checkpoint control does not close the physical three-PC,
50 GB, 72-hour LAN or WAN gates. It does not exercise real sleep/resume,
Wi-Fi switching, native NATs, mobile CGNAT, provider port 99, or a signed
release candidate.

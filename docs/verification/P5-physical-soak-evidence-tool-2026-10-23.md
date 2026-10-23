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
`C65F2F94A2F28522DE16BAA64D312D5A15B560C9D57D672CDBC2E6958B353F9C`.
That exact source was copied to the installed Server 2022 and Server 2025
guests and run with Windows PowerShell 5.1 against their previously
synchronized target/source folders. Both captures exited 0. Each measured
four files, zero non-metadata directories and 1,966,080 bytes, with matching
tree SHA-256
`0700E4CCB67970D65853E8F14E419EA99FF4B4F2C10A506BDEE5CBD51F1FAA27`.
The host comparison exited 0 with `match: true`, no issues, a common run ID
and capture starts about one second apart. The final JSON evidence is under
`artifacts/vm/tree-snapshot-2026-10-23/kit-*`.

A local changed-file fixture produced two valid captures but comparison
exited 1 with `tree-mismatch`. A separately marked **synthetic** 73-report
sequence with corresponding reported capture/comparison times passed the
72-interval and elapsed-time audit, while omitting hour 37 exited 1 with
`insufficient-hours` and `missing-or-duplicate-hour`. The host Windows
PowerShell 5.1 comparison and synthetic audit both exited 0 using
process-scoped `-ExecutionPolicy Bypass`, since that host blocks unsigned
scripts by default. These controls verify the reporter's decisions; the
synthetic sequence is not elapsed-time soak evidence.

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

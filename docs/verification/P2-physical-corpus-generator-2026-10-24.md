# P2 physical corpus generator control, 2026-10-24

This is tooling preparation, **not** a 50 GB three-PC LAN result. The new
`build/New-PeerSyncGateCorpus.ps1` uses a new run GUID under
`PeerSync-GateCorpus`, writes 100,000 1 KiB small files, a 24-level deep tree
with a 4 KiB file at each level, and eight large random-content files that
bring generated content to exactly 50 GiB (53,687,091,200 bytes). A small
`owner.json` marker is additional metadata. The generator refuses an existing
run, reparse-path traversal, a report inside the synchronized root, and a
source volume with less than twice the content bytes plus 5 GiB free. It
never removes a previous or partial run. The operator pauses the source test
folder for generation, resumes it afterward, and uses the existing tree tool
to verify all three peers. See [physical soak instructions](../physical-soak.md).

Under Windows PowerShell 5.1, an 8,388,608-byte control generated 32 small,
three deep and two large files in a fresh ignored test root. Its report had
`status=complete`, 37 content files and the exact requested content bytes.
Independent enumeration found 38 files including `owner.json`, with exactly
8,388,608 content bytes. `Measure-PeerSyncTree.ps1` completed over the same
root. Reusing the run ID, requesting the default 50 GiB on this low-space
development drive, and placing the output inside the root each failed before
creating a new target or report.

The generator's SHA-256 is
`BB2F455322820935C711A8D9B9DF48D517BF24D333BC278B6C595D14BC4EC837`.
`Build-PeerSyncFieldKit.ps1` includes that exact script and hash in the README.
A development kit for version 1.0.16.0 was built at
`artifacts/deploy/PeerSync-P5-field-kit-1.0.16.0-corpus-20261024.zip` (233,320,953
bytes, SHA-256
`9BCC7A99AA8731A9AD8A1B4D4449A5F2F3EFC305F1DE15A0DEE97731C71AAA9C`).
The ZIP entry's SHA-256 matches the source script. The retained small-fixture
report and tree snapshot are under
`artifacts/tests/gate-corpus-control-20261024/`. The full 50 GiB run was not
started: this host does not have the required free space or three physical
LAN PCs in the current setup.

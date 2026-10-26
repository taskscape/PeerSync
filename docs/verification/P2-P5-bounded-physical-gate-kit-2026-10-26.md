# Bounded physical gate kit, 2026-10-26

The physical LAN/WAN/soak gates remain open. This scripts-only kit lets the
three already installed client PCs use the revised [10 GiB and three-hour
requirements](../requirements/three-hour-ten-gib-test-budget.md) without
installing another service build.

`build/Build-PeerSyncPhysicalGateKit.ps1` produced
`artifacts/deploy/PeerSync-physical-gates-f999a931.zip` from clean tracked
revision `f999a931766e50c3a4971817de355077d5a3bd2a`. The 21,738-byte
ZIP has SHA-256
`D035B7EDA849FDDCB0B6097B262289095E46F7D5CF52A31CD7A563C4CE43A427`.
It contains five PowerShell tools, the physical-soak procedure, the bounded
requirement, a manifest and a README. No installer, folder key, password or
private certificate is included.

The ZIP was reopened locally: all nine entries were present and all seven
script/document SHA-256 values matched the manifest. The manifest reports a
9,663,676,416-byte corpus default, a 10,737,418,240-byte maximum test-tree
content cap, three active hours, 100,000 small files and three hourly
intervals. The corpus generator's focused 1 MiB control completed with 14
files and the exact requested content bytes. A synthetic four-checkpoint
audit passed at three scheduled hours; removing the final checkpoint failed
with `insufficient-hours` and `insufficient-elapsed-time`.

An operator must still transfer the ZIP, verify its external SHA-256 on each
PC, follow `docs/physical-soak.md`, and return the generated tree reports,
operation journals, topology and installed-version records. Local package
integrity and synthetic controls are not physical convergence or soak results.

Windows PowerShell 5.1 also built the kit with exit 0 in a separate local
control, and ran the revised generator on a fresh 1 MiB fixture with exit 0,
14 created files and exactly 1,048,576 content bytes. These controls exercise
the shell used by the physical field instructions; the default 9 GiB corpus
still needs the actual three-PC run.

## First physical PC preflight, 2026-10-26

The first PC's installed `PeerSync` service is Running at version 1.0.26.0.
Its already shared test folder, `C:\PeerSync-Test\Physical-Two-PC-20261024`,
contains 565,872,452 native content bytes before corpus generation. Adding
the default 9,663,676,416-byte corpus would produce 10,229,548,868 bytes,
leaving 507,869,372 bytes below the 10 GiB test-tree cap for operations. The
same volume had 33,876,029,440 bytes free at preflight, above the generator's
24,696,061,952-byte native-plus-block minimum. Recheck space and content
immediately before generation; these values are a dated snapshot.

The 21,738-byte kit ZIP was copied into that shared folder and its SHA-256
matched the builder value above. The installed CLI's `dump-index --json`
listed `PeerSync-physical-gates-f999a931.zip` as a live 21,738-byte file. The
local status showed connected authenticated sessions for two remote devices
in the folder. This does not prove either recipient received the ZIP, that the
third PC is on the same LAN, or that the 9 GiB physical corpus has run.

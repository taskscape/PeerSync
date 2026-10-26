# Current-source physical client upgrade, 2026-10-26

The first physical client was upgraded from 1.0.26.0 to the development-signed
1.0.28.0 build so the remaining field checks can exercise the scanner source
used by the passing 100,000-file local benchmark. This is an installed-client
upgrade check, not a three-PC corpus, soak, release-signing or production
rollout result. The second and third clients have not yet been verified at
1.0.28.0.

`build/Build-Installer.ps1` produced the x64 setup from tracked source
`e3be5dca83e07a821ab75fe567b3aa3de915c9fb` with explicit Inno Setup
and development signer arguments. The source manifest reports
`workingTreeDirty: true` because the unrelated untracked `Sync.zip.tmp` was
present; `git diff --name-only HEAD` found no changed tracked source input.
The 166,905,112-byte setup has SHA-256
`9B763AB40D19B7602EB6EEDA1E1C012AFC92806CF282CC264F14D09ADB3AB853`.
Its Authenticode status is `Valid`, signed by `CN=PeerSync Development` with
thumbprint `6AE1CBFC31F1DB189AF03A82D60C535AE8E0A7BD`. The manifest
explicitly marks `localDevelopmentOnly: true` and
`publicReleaseSigning: false`; this package cannot close the release gate.

The reusable field-kit builder and installer wrapper were committed as
`e4fba08`. `build/Build-PeerSyncClientUpgradeKit.ps1` packed the exact setup,
its manifest, a public-only certificate, the PowerShell 5.1 upgrade script,
launcher and README into
`artifacts/deploy/PeerSync-client-upgrade-1.0.28.0-x64.zip`. The 166,914,504-
byte ZIP has SHA-256
`C676C539105A9C001B690B88A0E459353244DE8D8C048A7218ACA2D2FB5C0182`.
The ZIP was reopened locally, all six entries were found, and the extracted
setup hash matched the manifest. Windows PowerShell 5.1 parsing passed; its
`-PreflightOnly` check returned `READY_TO_INSTALL` and an incomplete-package
negative control returned `FAILED` with exit 1. Those checks did not perform
an installation on the other two clients.

The first PC's retained before/after records are under
`artifacts/field/client-1.0.28.0-first-20261026/`. Before setup, the
PeerSync service was Running at 1.0.26.0. Silent setup exited 0; its log
reports successful installation with no restart required. After setup, the
service, app and CLI files all report 1.0.28.0 and valid signatures from the
same development signer; the service is Running and CLI status reports
`productionSyncAvailable: true` with connected remote sessions. Four sampled
native files, including the earlier third-to-first check, retained identical
SHA-256 values across the upgrade. These checks do not prove every file in
the folder was compared.

The upgrade ZIP was copied into the existing first-PC shared test folder,
`C:\PeerSync-Test\Physical-Two-PC-20261024`, with an identical SHA-256.
The first PC's installed `dump-index --json` listed it as a live 166,914,504-
byte file. Recipient arrival and installation remain unverified. Operators
must extract it outside the synchronized folder, exit the tray app, then run
the included `Run-PeerSync-Upgrade.cmd` elevated on the second and third PCs;
the wrapper verifies the setup, signer, version, installed service and CLI,
and emits a local JSON report.

The existing shared tree now contains 1,427,599,907 native content bytes.
Adding the required 9 GiB corpus there would exceed the 10 GiB test-tree cap.
Before the physical LAN run, either use a fresh shared test folder or move
obsolete test artifacts out of this tree after all recipients have finished
with them. Recheck the exact content total and free space before generation;
no corpus was started on the first PC as part of this upgrade.

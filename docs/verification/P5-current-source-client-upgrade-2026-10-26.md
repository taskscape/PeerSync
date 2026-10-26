# Current-source physical client upgrade, 2026-10-26

The first and second physical clients were upgraded from 1.0.26.0 to the
development-signed 1.0.28.0 build so the remaining field checks can exercise
the scanner source used by the passing 100,000-file local benchmark. This is
an installed-client upgrade check, not a three-PC corpus, soak,
release-signing or production rollout result. The third-PC operator reports
installing 1.0.28.0, but its installed version and service state have not yet
been independently verified.

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
negative control returned `FAILED` with exit 1. The second-PC installation is
recorded below; these local checks do not verify the third PC.

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
byte file. The second-PC recipient's file had the identical SHA-256. Its
administrator share and Task Scheduler service were reachable through Windows
instrumentation; the tray app was not running. The ZIP was extracted outside
the synchronized folder to `C:\Temp\PeerSync-client-upgrade-1.0.28.0-20261026`
on that PC. A one-time SYSTEM task ran the included validated upgrade script
and was deleted after it finished. Its preserved JSON report records previous
version 1.0.26.0, source revision `e3be5dc`, setup hash and signer matching
this package, setup exit 0, installed version 1.0.28.0, service Running, CLI
status exit 0 and no error. The task returned 0 and printed `READY`. The
service, app and CLI executables were independently read through the
administrator share: all report version 1.0.28.0 and valid development
signatures. Three sampled shared-file hashes, including the upgrade ZIP,
matched their before-upgrade values. Two newly created 4 KiB random files
then arrived in opposite directions between the physical PCs with exact
SHA-256 matches. The retained report, setup log, signed-file and file-byte
checks are under `artifacts/field/client-1.0.28.0-second-20261026/`.

The third PC was operated separately by its user. Its reported setup is not
yet an independently verified installed-client result. It should run the
included wrapper from outside its synchronized folder and retain its local
JSON report; no folder key is needed for that upgrade.

An initial 1,427,599,907-byte size estimate incorrectly included the root
`.psp` metadata. The tree verifier excludes that metadata from the 10 GiB
content cap. The corrected packaged PowerShell 5.1 preflight measured
732,810,649 counted content bytes and 694,836,644 `.psp` metadata bytes on
the first PC. The projected content after a 9 GiB corpus is 10,396,487,065
bytes, leaving 340,931,175 bytes below the cap. The read-only preflight
returned `status: ready` without creating a target or report, and the volume
had 42,220,085,248 free bytes against the generator's 24,696,061,952-byte
minimum. Recheck content and free space immediately before generation and
keep later soak writes inside the remaining headroom. No corpus was started
on the first PC as part of this upgrade.

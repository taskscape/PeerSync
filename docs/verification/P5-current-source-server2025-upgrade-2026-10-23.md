# Current-source Server 2025 upgrade, 2026-10-23

The disposable `PeerSync-P3-Server2025-Clean` Hyper-V guest was running the
previous development-signed 1.0.11.0 service under `NT SERVICE\PeerSync`.
Its update policy was disabled after the earlier feed tests. The guest already
trusted the same public development signer used for this check; no private
key was transferred.

From revision `c7eecb6fdcab7d61153b729a9d3512ea4432b1d3`,
`build/Build-Installer.ps1` built the x64 self-contained 1.0.12.0 package
with Inno Setup 7.1. Its manifest SHA-256 matched the package, and both host
and guest verified a valid timestamped Authenticode signature from the
expected development signer. The exact 166,765,472-byte package was copied
over PowerShell Direct; the guest's SHA-256 matched the host's before setup.
The package and build log are in `artifacts/installer/`.

Silent setup used `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=autostart`
and exited 0. Its retained guest log at
`C:\Temp\PeerSync\upgrade-1.0.12.log` had no exception/error/failure line
and recorded that Windows did not need a restart. After installation:

- The `PeerSync` SCM service was Running at 1.0.12.0, and the default
  installed `psp status --json` client exited 0.
- The `PeerSyncUpdateInstall` scheduled task still ran as SYSTEM and pointed
  at the installed `PeerSync\tools\Update-Helper.ps1`.
- The installed helper's SHA-256 matched the exact repository file:
  `863E6E2F4DC1C69E3DED65E9343EE3C4215FA5A11E737A0D500596FEE5D9317A`.
- The prior `config/settings.json` SHA-256 was unchanged across setup.
- The installed read-only crash-policy audit found all three client
  executables excluded from automatic WER reports, with neither global nor
  per-executable LocalDumps configured on this guest.

This verifies a current-source signed development upgrade and installed
policy on Server 2025. It does not exercise Setup exit 3010, an actual reboot
and subsequent staged-slot retirement, uninstall cleanup, Server 2022 or
Windows 11, a production signer, or an independently reviewed host. Those
requirements remain open. The separate clean Server 2022 VM was Running but
had lost its guest heartbeat during this check, so it was not modified.

# Open-dashboard installer upgrade repair, 2026-10-24

The physical second PC's silent 1.0.16.0-to-1.0.22.0 upgrade initially failed
while `PeerSync.exe` was still running in an interactive user session. Setup
stopped the service, then failed to replace `app\PeerSync.exe` with
`DeleteFile` access denied and exit code 5. The service was immediately
restarted; after closing only the dashboard process, the same signed setup
completed with exit 0. The service is Running, settings and file hashes were
preserved, and subsequent test files converged in both directions. The
[field record](P4-two-PC-cross-version-field-2026-10-24.md) retains the exact
upgrade and file evidence.

The installed app's ordinary window-close handler cancels closure and hides
the window to keep its tray process alive. The Inno script had
`CloseApplications=yes`, which asks Windows Restart Manager to close processes
using files that setup needs. In this field case, the graceful close did not
release the executable. Current source changes the installer to
`CloseApplications=force` and retains `RestartApplications=no`. Inno's
[documented directive](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)
forces closure if the graceful attempt does not work. The packaged field kit
also instructs the operator to choose the tray menu's `Exit app` first, which
avoids forced closure when attended. The service remains independent of the
dashboard.

An unsigned-development x64 ISCC compile of the changed script succeeded and
produced `artifacts/tests/installer-close-apps/PeerSync-Setup-CloseApps-Compile.exe`
(166,911,043 bytes). This is syntax/package validation, not a signed or
installed upgrade. The following field check therefore left the dashboard
deliberately open and compared service, settings and joined-folder state.

That field check subsequently passed on the first physical PC. The signed
development x64 1.0.23.0 installer (166,904,416 bytes, SHA-256
`A3CAF857296B42175A455ADB46F51651B8CFF9C6DB738BD318C6E32FFCA1CCC0`)
was built from revision `57b95c9` and verified with a Valid Authenticode
signature. An installed 1.0.21.0 dashboard process was deliberately left
running in the interactive session while `/VERYSILENT /SUPPRESSMSGBOXES
/NORESTART` setup ran. Setup exited 0; its log records `Shutting down
applications using our files. (forced)`, and the dashboard process exited.
The installed app advanced to 1.0.23.0 and the PeerSync service was Running.
The settings SHA-256 and all seven pre-upgrade text-file hashes in the joined
test folder matched the saved pre-upgrade snapshot. All three installed
executables had Valid Authenticode signatures, and `psp status --json`
reported authenticated, clock-accepted sessions with both existing remote
devices after the restart. The bounded before/after JSON snapshots and setup
log are in `artifacts/field/client-force-close-upgrade-20261024/`.
The first PC then created `first-pc-1.0.23-check.txt` in its joined test
folder; it reached the second physical PC within six seconds with matching
native SHA-256
`49B51B62C763DF9FD6E52B9D80981725B475C7E848055CF325041427F92C4FE2`.

This verifies the forced-close upgrade path on this physical Windows client.
It does not establish standard-user UI behavior, other OS builds, third-PC
installation, or the release-signed deployment gate.

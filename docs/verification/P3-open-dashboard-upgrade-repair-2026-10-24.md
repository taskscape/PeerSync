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
installed upgrade. A later field or VM upgrade with the dashboard deliberately
left open must verify that setup closes it, exits 0, restarts the service, and
preserves settings and joined-folder bytes before this repair is called
deployment-verified.

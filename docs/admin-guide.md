# PeerSync administrator guide

This guide covers the installed client service. The central server is
administered separately in [rendezvous-operations.md](rendezvous-operations.md).
Keep the installer and device certificate private keys on their intended
machines. Current physical-network and release gates are listed in
[plan.md](../plan.md).

## Install and initialize

Run the client setup elevated on each Windows PC. It installs the `PeerSync`
service under the `NT SERVICE\PeerSync` virtual account, the dashboard and
`psp.exe` under `C:\Program Files\PeerSync`, and a local `PeerSync Users`
group for authorized log readers. Setup calls
`C:\Program Files\PeerSync\service\PeerSync.Service.exe --init` to prepare
`%ProgramData%\PeerSync`, a per-machine P-256 device certificate with a
non-exportable private key, a listener port and device settings. Do not reuse
the certificate across PCs. Setup keeps application data during an ordinary
upgrade and checks that the service stops before replacing binaries.

The installer supports silent operation with
`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`; use an elevated process and
inspect its exit code and service state afterward. For a manually staged
service, `--init` must run elevated before starting it. Restrict write access
to `%ProgramData%\PeerSync\config`, `state` and certificate keys to the
service and administrators. Operators who use the local control pipe must be
authorized in the device settings; membership in `PeerSync Users` alone is
for diagnostic-log reading.

On an unpatched Windows Server 2022 evaluation guest, the runtime rejected
`--init` with `Your Windows doesn't fully support CET. Please install all
available Windows updates.` Setup had copied files but could not initialize
the service. Installing the available Windows updates and rebooting made
`--init` and the service start succeed on that guest. If this exact error
appears, update and reboot Windows before retrying Setup; verify both
`Get-Service PeerSync` and `psp status --json` afterward.

## Signed automatic updates

Automatic updates are off until an administrator installs a trusted publisher
certificate, sets an HTTPS manifest URL, and enables the policy. The service
can read that policy and stage a signed update, but only administrators and
SYSTEM can change the policy or the trusted copy used for installation. Setup
registers a daily 03:00 PeerSyncUpdateInstall task under SYSTEM. Check it
after an upgrade, along with the service; older silent Inno builds could
return exit code zero after a post-install script error.

Run this from elevated PowerShell, replacing the URL and public certificate
with the approved release publisher. The .cer file contains no private key.

~~~powershell
& 'C:\Program Files\PeerSync\tools\Set-UpdatePolicy.ps1' -Enable -ManifestUrl 'https://updates.example.com/peersync/stable.json' -PublisherCertificate 'C:\Temp\PeerSync-Publisher.cer'
Get-ScheduledTask -TaskName PeerSyncUpdateInstall | Select-Object TaskName, State, @{n='User';e={$_.Principal.UserId}}
~~~

To stop future checks and scheduled installations, run:

~~~powershell
& 'C:\Program Files\PeerSync\tools\Set-UpdatePolicy.ps1' -Disable
~~~

A task already installing a package must finish before disable takes effect.
The signed offer expires after at most 30 days; stale or modified packages
are rejected and retained for diagnosis.
If Setup returns 3010 (restart required), the pending package remains until
the next scheduled helper run after a Windows reboot. The protected attempt
receipt records when Setup started and its 3010 result; the helper verifies a later boot and the
signed package again, then removes its own slot only if the installed version
matches, the service is running, and the SYSTEM task is intact. A missing
receipt, pending restart, failed health check or expired offer keeps the slot
for administrator diagnosis.

To rotate the publisher, first disable updates and wait for any running
`PeerSyncUpdateInstall` task to finish. Inspect `%ProgramData%\PeerSync\updates`
for GUID-named `.ready` directories. An old staged package cannot pass the
new pin, and its presence prevents the service from fetching another offer;
archive or remove each old slot under administrator control after recording
its manifest, setup hash and reason for disposition. `Set-UpdatePolicy.ps1`
refuses a different publisher pin while a `.ready` slot remains. Then install
the new publisher's public certificate in the appropriate machine trust
stores, enable the new policy with its `.cer` file, restart the PeerSync
service so it checks the new feed, and verify that the SYSTEM task accepts a
newly staged package. Keep the prior pin and trust only while an approved
transition needs them; a single policy accepts one exact publisher at a time.

If a publisher key is compromised or its certificate revoked, disable updates
immediately, prevent the daily task from starting another installation, and
inspect any task already running before changing trust. Remove the old
publisher from machine trust according to the organization's certificate
policy and disposition its pending packages as above. Resume only with a
separately trusted, signed setup and a new pinned publisher. An exact pin
checks identity, but it is not a substitute for certificate revocation and
chain-trust validation. Release signers, rotation and revocation still need
an installed-host check before the release gate closes.

Release operators first sign the installer with the pinned Authenticode
certificate, upload it to a direct HTTPS URL, then produce and publish the
detached manifest using the same certificate's non-exportable signing key:

~~~powershell
& .\build\New-UpdateManifest.ps1 -SetupPath '.\artifacts\installer\PeerSync-Setup-1.0.8.0-x64.exe' -Version '1.0.8.0' -SetupUrl 'https://updates.example.com/peersync/PeerSync-Setup-1.0.8.0-x64.exe' -CertificateThumbprint 'REPLACE_WITH_40_HEX_CHARACTERS' -OutputPath '.\artifacts\installer\stable.json'
~~~

Publish the exact generated bytes to the URL in the policy. The manifest
contains the installer version, URL, SHA-256 digest, byte count and expiry;
its RSA-PSS/SHA-256 signature covers the exact payload bytes. The service
fetches it at startup and daily, rejects redirects, and downloads only a
newer signed version. The scheduled helper rechecks the manifest, package
digest and Authenticode publisher before running silent Setup. It also builds
a current code-signing certificate chain with online revocation checking for
every non-root certificate. If revocation data cannot be obtained, the helper
fails closed and retains the staged package; deployment networks must allow
the publisher CA's revocation endpoints. A failed validation does not replace
the installed service.

For diagnosis, inspect Get-ScheduledTaskInfo -TaskName PeerSyncUpdateInstall,
Application events 4301 (setup), 4302 (failure) and 4303 (post-restart slot
retired) from PeerSync, and the service logs. A
.ready directory under the ProgramData PeerSync updates directory remains
after failure. Do not run a setup from that directory manually; use the
signed helper after resolving the failure. The development certificate and
test URL used in the disposable VM are not release credentials or a
production update feed.

## Network and permissions

The service listens on its configured `listenPort` for authenticated QUIC
(UDP) and TCP/TLS. Allow inbound traffic on that port from intended peers.
Local discovery uses the LAN and should stay scoped to the local subnet.
Tracker/relay operation needs outbound TCP 99 to the configured rendezvous
server; the central server also exposes UDP 99 for HTTP/3 probing. Firewall
and provider rules both matter. See
[the two-PC runbook](peersync-service-two-pc-runbook.md) for narrow test
firewall rules and [rendezvous-operations.md](rendezvous-operations.md) for
the server's TCP/UDP 99 setup.

Device settings are stored in
`%ProgramData%\PeerSync\config\settings.json`. A registered folder has
separate settings including `RequireApproval`, `ArchiveDays`, predefined
hosts, tracker and relay switches. Make a backup before changing settings,
validate JSON and use the authenticated control API or dashboard to apply
folder changes. A changed active folder briefly drains its sessions and
reopens with the new policy; other folder engines stay registered, though
shared peer sessions may reconnect. If reopening fails, the changed folder
remains paused and can be resumed after fixing the cause.
For folder changes, restart the service only if the dashboard reports that a
network policy could not be refreshed. Folder keys must never be placed in
this file, logs or support bundles.

## Health, logs and recovery

```powershell
Get-Service PeerSync
& 'C:\Program Files\PeerSync\cli\psp.exe' status --json
& 'C:\Program Files\PeerSync\cli\psp.exe' probe sync.taskscape.com:99
& 'C:\Program Files\PeerSync\cli\psp.exe' diagnostics --output 'C:\Temp\peersync-client.zip'
```

Structured logs are under `%ProgramData%\PeerSync\logs\service`; warning
and error summaries also reach the Windows Application Event Log. The
diagnostic command makes a new ZIP with bounded logs and environment
metadata. Compare session IDs and UTC times across peers. If the service
fails during startup, inspect its Application event and the latest startup
journal. `PeerSync.Service.exe` can run in the foreground after stopping the
Windows service; never run both against the same database and listener.
Detailed event names, privacy rules and failure classification are in
[diagnostics.md](diagnostics.md).

Large scans may create `scan-observations-*.db` and matching `.lock` files
beside the private folder index. They contain relative file names and native
metadata, so keep them under the same restricted ACL and out of support
bundles. A later scan discards an abandoned stage after its owner lock has
closed; do not remove a stage while the service is running.

## Crash-report policy

Setup excludes `PeerSync.Service.exe`, `PeerSync.exe` and `psp.exe` from
automatic machine-wide Windows Error Reporting. It leaves any exclusion
already created by an administrator alone, maintains its own exclusions on
upgrade and removes only its own entries at uninstall. Check the policy after
install or upgrade with the packaged read-only audit:

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive `
    -ExecutionPolicy Bypass -File 'C:\Program Files\PeerSync\tools\Audit-CrashPolicy.ps1' `
    -Component Client
```

Require `allAutomaticReportsExcluded: true`. Review
`globalLocalDumpsConfigured` and each `localDumpsConfigured` flag before
production use; the audit also distinguishes an empty registry key from a key
with values. It reports no dump-folder paths. An administrator-configured
global or per-executable `LocalDumps` policy can still create dumps even when
automatic reporting is excluded. If local dumps are required by your
organization, restrict them as secret material and never attach them to a
PeerSync support bundle. A privileged live/full dump of a running process may
contain folder keys; PeerSync cannot remove keys it must actively use from
an administrator-created memory capture. See the [approved security
boundary](proposals/P5-crash-dump-boundary.md) and [Microsoft's LocalDumps
documentation](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps).

For an uninstall that preserves synchronized files, use the client
uninstaller without data removal. `PeerSync.Service.exe --remove-data` is an
explicit destructive administration command: it removes PeerSync application
data and its device identity, so old device approval and local state will not
survive reinitialization. Export needed diagnostics and verify the intended
machine before invoking it. The
[two-PC runbook](peersync-service-two-pc-runbook.md) contains the full test
deployment and teardown sequence.
After either uninstall path, confirm that `Get-Service PeerSync` and
`Get-LocalGroup 'PeerSync Users'` find no entry, the
`PeerSyncUpdateInstall` task is gone, and the synchronized folder files remain.
The remove-data path should also remove `%ProgramData%\PeerSync` and the
machine's PeerSync device certificate; the keep-data path preserves them.

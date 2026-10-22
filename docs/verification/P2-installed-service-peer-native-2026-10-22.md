# P2 fixed installed-service authenticated peer probe — 2026-10-22

The local `PeerSync` Windows service was already running as `NT SERVICE\PeerSync`
from `PeerSync-win-x64-20261022T092247Z-8a9b81c3.zip` on configured port
45973. Its existing disposable `C:\PeerSync-Test\Share` contained the indexed
42-byte `smoke.txt`. The standalone source-checkout
`spikes/InstalledServiceProbe` executable was built in Release with zero
warnings/errors. It selected that active folder through the fixed
`ControlStatusClient.CreateForInstalledService` endpoint, whose connection
verifies the SCM service owner and kernel server PID. It obtained the share key
through the audited `GetFolderKeys` RPC without printing it.

The probe generated a separate ephemeral P-256 device certificate, state
database and empty share under a unique temporary directory. It started a real
`InstalledFolderHost` peer on port 41128 with LAN discovery disabled. A narrow
TCP/UDP firewall rule for the probe executable and loopback address was created
before the peer listened; its four rules were removed after the run. No
installed-service settings, folder registration, certificate or source file
were changed.

The peer initially reported UTC 11 minutes ahead through an injected
`TimeProvider`; the Windows clock was untouched. It dialled the fixed SCM
service's loopback endpoint. The default verified client observed the peer's
certificate-derived identity with `Skewed` clock status and production
availability false; the source file did not appear in the probe share. After
the probe corrected only its injected clock, the supervised reconnect produced
an `Accepted` status, production availability true and a SHA-256-verified copy
of `smoke.txt` in the probe share. Disposing the peer returned the installed
service to availability false with no connected peers. The probe completed in
1.743 seconds and reported:

```json
{"status":"passed","scmInstanceVerified":true,"authenticatedClockRejected":true,"recoveredTransferVerified":true,"disconnectObserved":true,"bytes":42,"elapsedSeconds":1.7431752}
```

The fixed service journal at
`C:\ProgramData\PeerSync\logs\service\5f4557ac82914cff8ffc7082b520f130\debug.jsonl`
contains six `clock.blocked`, two `clock.accepted`, one
`service.folder_peer_ready` and one `service.folder_sync_pass` event from this
run, along with index receipt and transport events. The transient
`AwaitingHello` events after a rejected or closed session are expected. The
packaged CLI collected 252 recent events from nine logs into
`C:\PeerSync-Test\diagnostics-installed-peer-probe.zip` (10 archive entries).
Review confirmed the known device-certificate thumbprint, installed share path
and package path are absent from the sanitized bundle. The probe's temporary
directory and firewall rules were absent after exit, and the SCM service
remained Running and responsive.

The reproducible elevated source-checkout command is:

```powershell
.\build\Test-InstalledServicePeer.ps1 -FolderPath 'C:\PeerSync-Test\Share' -FileName 'smoke.txt'
```

The wrapper builds with locked restore, reads the installed listener port,
reserves a separate TCP/UDP port, creates probe-executable loopback firewall
rules before launch and removes its exact rules in `finally`. An independent
wrapper run selected port 30527 and passed the same four assertions in 2.659
seconds; the probe-specific rules were removed. The wrapper needs an elevated
PowerShell session and the .NET 10 SDK on the development PC.

After the full current-source verifier passed, the service and CLI were
republished as
`artifacts/deploy/PeerSync-win-x64-20261022T094722Z-0f777d3a.zip`
(SHA-256 `A9AFAE251D7E31C4D628C3593F1159BE416597B310AE663A320DD52904912C67`).
The prior installation was preserved at
`C:\PeerSync-Test\service-package-backup-20261022-114744`; the SCM service
restarted as Running and the default verified CLI read instance
`dabfbaec-1a2e-4e22-9f72-38c9e7660b7b` with no peers. The wrapper then
selected port 26275 and passed all four assertions against this new installed
package in 1.722 seconds. Its temporary firewall rules were absent at exit.
The package's CLI collected 422 recent events from nine logs into
`C:\PeerSync-Test\diagnostics-installed-peer-current.zip` (10 entries).
The sanitized bundle contains clock rejection/admission, peer-ready and sync
pass events; the device certificate thumbprint, known test-share path,
installation path and folder-key field are absent. The service remained Running.

The optional `-RenderUi` run used the same default SCM-verified control client
and the actual WPF `ClockStatusWindow`/controller on an active desktop. It
passed against the fixed installed service with `renderedUiVerified=true` in
3.764 seconds. Captures at `artifacts/clock-ui/native-scm-skewed.png`,
`native-scm-recovered.png` and `native-scm-disconnected.png` were inspected:
the skew view shows `+11.00`, exchange blocked and unavailable; recovery shows
`0.00`, Clock accepted and available; disconnect shows no peer clock sessions
and unavailable. The recovered capture briefly contains two rows for the same
peer while two clock samples are active; the service journal records one
`service.folder_peer_ready`/sync pass for that run, and both rows clear on
disconnect. This is transient status history, not evidence of two independent
sync workers. The run selected port 42095 and removed its probe firewall rules.
The App's unavailable copy was subsequently corrected to say "not available
right now" rather than "not available in this build". All 14 focused App
tests passed, and a final `-RenderUi` run against the same installed service
passed in 3.765 seconds on port 44654. It overwrote the three captures with
the corrected copy; the disconnected capture was re-inspected and shows the
new wording. The wrapper removed the probe firewall rules.

This verifies an authenticated production transport, clock block/recovery,
file transfer and fixed installed-service client on one Windows machine. It
does not prove physical two-PC LAN discovery/throughput, Windows Server
interoperability or the 72-hour soak. Those P2 checks remain open.

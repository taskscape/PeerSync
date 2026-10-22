# P2 foreground console debugging — 2026-10-22

After the latest canonical verifier and package build, the installed SCM
service was stopped before launching
`C:\Program Files\PeerSync\service\PeerSync.Service.exe` directly from an
elevated PowerShell prompt. The current package
`PeerSync-win-x64-20261022T092247Z-8a9b81c3.zip` loaded the installed
certificate and one registered folder, completed its initial scan, opened the
QUIC and TCP listeners on port 45973, and reported `service.control_ready` in
`C:\ProgramData\PeerSync\logs\service\5a7523af12364e66b4dfab4a48edd2fb`.
This confirms the console path can use the installed settings, state and
certificate when SCM is stopped.

The default `psp status --json` client returned `Service unavailable:
IOException` during this console session. This is expected because that client
requires the SCM service to be Running and verifies the service owner and PID;
it deliberately does not attach to an arbitrary console process. The console's
`LOGS` path and structured events are the diagnostic source while debugging in
the foreground.

Ctrl+C stopped the console host gracefully. Its log contains
`service.stopped`, `discovery.lan_stopped` and
`service.peer_network_stopped`, and contains no
`discovery.lan_announce_failed` warning. The cancellation-time
`ObjectDisposedException` shutdown race is fixed in source and covered by
`ShutdownDuringLanAnnouncementDoesNotLogDisposedSocketAsFailure` in the 48-test
Net suite.

The packaged diagnostics command collected 166 events from nine logs into
`C:\PeerSync-Test\diagnostics-final-package-after-console.zip`. Inspection found 10 entries and
confirmed the device-certificate thumbprint, test share path, install path and
user profile path are absent. SCM was restarted immediately after the console
exited. The default client reconnected to the SCM instance, which returned
`productionSyncAvailable=false` with no peers. The complete package archive,
rollback directory and canonical verifier evidence are recorded in
`docs/verification/P2-current-source-verification-2026-10-22.md`.

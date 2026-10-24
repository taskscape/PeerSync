# Third-PC transient-network notification flood, 2026-10-24

The third PC reported repeated Windows PeerSync notifications reading
`PeerSync error: SocketException` after joining the folder from a mobile
hotspot. At the same time, the first PC's installed control status gained a
new connected, clock-accepted peer for the folder. That establishes an
authenticated session, not completed file convergence or the cause of the
third PC's socket failure. Its local diagnostics are still needed to identify
the failing operation and native socket error.

The source path explained the notification flood: `ControlStatusRuntime`
projected every diagnostic warning and error into `ControlEvent.Errors`, and
`DashboardViewModel` treated each new event timestamp as a fresh toast.
Failed LAN/public connection candidates and tracker retries are warnings;
the network manager can authenticate through another candidate. A transient
warning is retained in the structured journal but no longer becomes a
user-facing error. Real `DiagnosticLevel.Error` events remain in the control
snapshot. The dashboard limits repeated notifications for the same error code
and source to once per 30 minutes, including across a control reconnect;
distinct error kinds still notify immediately. Pending-device and overwritten
version notices are unchanged.

Focused release checks: `PeerSync.App.Tests` 22/22 and
`PeerSync.Service.Tests` 59/59 passed with no skips. The new regressions
verify warning preservation in the raw journal, error-only control projection,
different error kinds, cooldown expiry and suppression across reconnects.
TRX files are under `artifacts/tests/third-pc-notification/`.

These are source checks. The currently installed third-PC package predates
this repair. Its own log classification, installed update and rendered Windows
notification behavior remain open. This does not close P3.2.5 or physical
WAN/NAT/file-convergence gates.

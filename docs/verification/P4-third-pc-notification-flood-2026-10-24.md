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

The development-signed x64 1.0.18.0 installer was built from commit
`a441cdb302221f75b5557219537f9f2087d5cc76`, with the same signing
certificate as the installed 1.0.16.0 package. Its Authenticode status is
Valid and its SHA-256 is
`C893DF2D783ADE60AFD032537FCF5F82814CEA2D668F7C70BA4A78DB71A8A042`.
The manifest marks the worktree dirty because an unrelated pre-existing
untracked `Sync.zip.tmp` remains; the repair itself was committed before
building. The package is development-signed, not a public release build.

The first PC upgraded silently from 1.0.16.0 to 1.0.18.0 with setup exit 0.
Its PeerSync service is Running, the installed App and service report
1.0.18.0, and `config/settings.json` retained SHA-256
`A9B29B4F662B62F6C712E61F8DE5E817E26109C2C86F978FC9E7AB7A46006243`.
The five existing test-folder files remained present with matching hashes,
including `post-upgrade-first-pc.txt` at
`1DA04139ED14F8290E4453DA841F42E46F3E38F580007C3D7B4597EB5878ACEB`.
The service reconnected to both prior peers with accepted clocks. The setup
log is retained at `artifacts/field/client-notification-upgrade-20261024/setup.log`.

The first PC's current raw journal shows a failed public TCP candidate to the
mobile peer's observed public address, followed by an authenticated QUIC
candidate to its `100.x` address. `Find-NetRoute` selected the Tailscale
interface for that QUIC path. This is a real connected peer across the
hotspot, but it is an overlay route and does not prove native Internet NAT
traversal or relay. The failed candidate is a normal retriable warning; it
would have generated the old repeated toast. The third PC's exact
`SocketException` and native error still require its own log output.

A later read of the first PC's installed 1.0.18.0 service journal found seven
tracker stream failures between 10:59 and 11:32 UTC. Each arrived about
319 seconds after a successful HTTP/2 tracker connection and each reconnected
successfully after the configured backoff. Those records contain only generic
`RpcException` and its HRESULT, so neither the server's reason nor its
relationship to the third PC's socket notification can be established from
them. No diagnostic ZIP from the third PC had arrived in the joined folder
at the time of this inspection.

Current source now records the gRPC status, elapsed session time and nested
native socket error on a tracker failure, plus a sanitized event after each
initial or periodic announcement write succeeds. This permits a later client
and server journal comparison without logging folder IDs or paths. The Release
Net suite passes 59/59 locally. The installed first and third PCs predate
these additional tracker fields; the exact cause remains open.

The current-source connection diagnostics now record the Windows native socket
error number on failed candidate and reconnect events, including a socket
exception wrapped by `IOException`. This will improve future captures; it
cannot recover the code from the third PC's earlier installed build. The full
Release Net suite passed locally (59/59) with a regression for the wrapped
socket case.

The third PC still runs the earlier package and needs this upgrade before the
notification fix can be observed there. Its rendered Windows notification
behavior and `third-pc-check.txt` file convergence remain open. This does not
close P3.2.5 or physical WAN/NAT/file-convergence gates.

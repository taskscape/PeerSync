# Peer listener restart collision — local source repair, 2026-10-25

During the physical third-PC WAN field check, an immediate SCM restart of
installed PeerSync 1.0.23.0 failed before creating the test file. A second-PC
reproduction showed the new process failing its listener bind with
`SocketException` 10048 at 19:46:17.675Z. The old process had logged
`service.stopped` at 19:46:17.199Z but did not log
`service.peer_network_stopped` until 19:46:19.032Z. SCM's Stopped state could
therefore precede release of the old QUIC/TCP ports. The field script worked
around this by waiting for the old process to exit; its corrected physical
LAN and mobile-hotspot runs passed. See
[overlay-isolated field evidence](P4-native-wan-overlay-isolation-2026-10-24.md).

`PeerNetworkRuntime.StartAsync` now treats only `SocketError.AddressAlreadyInUse`
as a retryable listener-bind collision. It closes any QUIC listener opened by
the failed attempt before retrying both QUIC and TCP binds at 250 ms
intervals for at most 15 seconds. Cancellation interrupts the wait, and a
persistent collision still fails startup. The journal records the first
retry and a successful recovery with elapsed time and retry count; it does
not log file paths or secrets.

An actual native TCP socket held the selected port until the new service
runtime emitted `service.listener_bind_retry`; the test then released the
socket and required `service.listener_bind_recovered` and a live runtime.
The focused `PeerNetworkEndpointTests` run passed all 13 cases, and the full
Release `PeerSync.Service.Tests` project passed all 72 cases with zero skips
on 2026-10-25. This validates the source-level retry and listener cleanup.
At this initial source-check point, it had not yet been packaged, installed
or repeated through the SCM on the physical PCs. Their binaries were still
1.0.23.0; that source-only result did not count as a deployed restart check.

## Installed first-PC collision, 2026-10-25

The signed 1.0.25.0 client was installed on both physical LAN PCs; see the
[upgrade record](P3-installer-service-exit-local-2026-10-25.md). On the first
PC, the installed stop/process-exit helper stopped service PID 67108. A
temporary exclusive TCP listener then occupied its configured port 45973
while SCM started the new service. The new PID 10944 journal recorded
`service.listener_bind_retry` with `address_in_use`, native error 10048 and
250 ms delay. After the temporary listener was released, the same run
recorded `service.listener_bind_recovered` after 281.1427 ms and one retry,
then opened its control pipe and authenticated TCP/QUIC peer sessions. The
service ended Running, and the installed CLI reported production sync
available with four connected clock-session rows. The bounded script and
sanitized event excerpt are under
`artifacts/field/first-pc-1.0.25-upgrade-20261025/`.

Two earlier attempts by the field script stopped before the collision check:
the first selected a UDP-only port, and the second misread a multi-line stop
marker in PowerShell. Both restored or retained a Running service. The
corrected attempt above proves the installed retry path. The other physical
PC, Server builds, long-running stability and release candidate remain open.

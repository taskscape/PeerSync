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
It has not yet been packaged, installed or repeated through the SCM on the
three physical PCs. Those installed binaries remain at 1.0.23.0 until a
subsequent signed upgrade; do not credit this source repair as a deployed
restart result.

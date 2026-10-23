# P4 probe correlation and transport diagnosis, 2026-10-23

`psp probe` now assigns one random session ID to its exact HTTP/2 (TCP) and
HTTP/3 (UDP) requests. The rendezvous `/probe` handler accepts only a 32-hex
hyphenated GUID, writes it on `rendezvous.probed` with the observed protocol,
and echoes it. The format matches the journal's `sessionId` for direct bundle
searches. The CLI reports whether each successful leg echoed the ID. Failures
include bounded categories and available HTTP, QUIC, socket, native and
HRESULT codes; exception messages are not printed.

Local release verification:

| Check | Result |
| --- | --- |
| `dotnet test tests/PeerSync.Rendezvous.Tests/PeerSync.Rendezvous.Tests.csproj -c Release --no-restore` | 17 passed, zero failed |
| `dotnet test tests/PeerSync.Cli.Tests/PeerSync.Cli.Tests.csproj -c Release --no-restore` | 6 passed, zero failed |
| `dotnet build src/PeerSync.Cli/PeerSync.Cli.csproj -c Release --no-restore` | Zero warnings and errors |
| `psp probe 127.0.0.1:1` | Exit 1; TCP classified `refused` with `ConnectionRefused` / native 10061; UDP classified `unreachable` with `HostUnreachable` / native 10065 on this host |

The loopback HTTP/3 host test verifies that the response and the written
diagnostic event carry the same ID, and a malformed ID is neither echoed nor
admitted to the journal. A real sanitized server ZIP retains that probe ID
and the later tracker rejection ID while excluding addresses and ports by
default. The same hosted test sends an invalid first tracker
message through an authenticated HTTP/2 stream and verifies that
`rendezvous.session_failed` records `InvalidArgument` under the observed
session ID. A certificate-authenticated relay stream without a token records
`rendezvous.relay_join_rejected` with a fixed code and no token value. The
server also logs tracker deadline/transport failure categories and relay join
cancellation categories through these bounded event paths. A revoked relay
offer reports `Aborted` to its waiting participant; a join wait deadline
reports `DeadlineExceeded`, while caller cancellation remains cancellation.
The refused loopback
check verifies the deployed CLI
JSON shape but does not test a deployed rendezvous endpoint or external UDP
reachability. Physical two-PC LAN/WAN forced-relay bundles and production
server certificate/network checks remain open under Gate P4.

A separate hosted relay test connects two distinct certificate identities to
the production `Relay.Pipe` service, forwards a valid byte, then sends a
65,537-byte chunk across its 64 KiB limit. The server returns `InvalidArgument`;
its collected ZIP retains `rendezvous.relay_forward_failed`, the joined
session ID and the status code without the relay token or network address.
This is a controlled local server failure, not the required forced-path
two-PC LAN/WAN bundle.

A separate HTTP/2 burst receives 120 successful `/healthz` responses and
429 responses on requests 121–123. The server records one sampled
`rendezvous.http_rate_limited` warning with
protocol and observed endpoint; the collected default ZIP keeps the event and
`http-429` code but removes the address. This proves that a request reaching
the limiter is distinguishable from a TCP/UDP request that never reached the
server, while the five-second sampling gate bounds journal/Event Log writes
under a sustained rejection flood. Every denial still increments the metric.
This does not claim external firewall coverage.

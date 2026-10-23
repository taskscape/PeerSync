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
| `dotnet test tests/PeerSync.Rendezvous.Tests/PeerSync.Rendezvous.Tests.csproj -c Release --no-restore` | 13 passed, zero failed |
| `dotnet test tests/PeerSync.Cli.Tests/PeerSync.Cli.Tests.csproj -c Release --no-restore` | 6 passed, zero failed |
| `dotnet build src/PeerSync.Cli/PeerSync.Cli.csproj -c Release --no-restore` | Zero warnings and errors |
| `psp probe 127.0.0.1:1` | Exit 1; TCP classified `refused` with `ConnectionRefused` / native 10061; UDP classified `unreachable` with `HostUnreachable` / native 10065 on this host |

The loopback HTTP/3 host test verifies that the response and the written
diagnostic event carry the same ID, and a malformed ID is neither echoed nor
admitted to the journal. The refused loopback check verifies the deployed CLI
JSON shape but does not test a deployed rendezvous endpoint or external UDP
reachability. Physical two-PC LAN/WAN forced-relay bundles and production
server certificate/network checks remain open under Gate P4.

# P2 TCP/TLS fallback connection layer

`PeerSync.Net/Tcp/` now provides a TCP control connection and up to three
independent data connections. Every connection negotiates TLS 1.3 with ALPN
`psp/1` and mutual device certificates. Both sides validate the remote
certificate-derived DeviceId against the caller's pin policy. A random
32-byte ticket is issued on the control connection, acknowledged before the
session is admitted, and required on each data connection. Data lanes must
present the control session's same DeviceId. Tickets are kept in memory,
cleared on disposal and excluded from diagnostics.

The listener uses TCP independently of QUIC's UDP socket, allowing both
transports to bind the same port. TLS handshakes have a bounded timeout and a
64-connection concurrency limit. Session data lanes are limited to three and
are released when disposed or when their session closes. Connection failures
record a bounded stage (`connect`, `tls`, `pin`, `ticket`, or `admission`), a
safe error category, HRESULT, and socket native error when available. Logs do
not include certificate names or exception text.

## Local verification

On 2026-10-21, `dotnet test tests/PeerSync.Net.Tests/PeerSync.Net.Tests.csproj
-c Release --no-restore` passed all 19 tests. The full canonical local
`./build/Verify.ps1` also passed with 37 Net tests and 1,663 tests overall. The
port-binding group is serialized with other QUIC tests and retries an
ephemeral port if another process claims its UDP side between TCP and QUIC
binds. The focused TCP suite also verifies that an unpinned certificate is
reported at the pin stage with an HRESULT and no certificate name. New loopback
coverage exercises:

- pinned mutual-TLS control and data connections, including bidirectional
  application bytes over each lane;
- three concurrent data lanes and rejection of a fourth;
- refusal of an unpinned control certificate;
- rejection of a valid pinned certificate presenting an unknown session
  ticket; and
- TCP and QUIC listeners binding the same numeric port.

## Remaining integration

This evidence covers connection authentication and lane attachment. The TCP
control stream is not yet wired to the shared Hello/folder-proof handshake or
to per-folder index and block transports. Connection-manager fallback,
reconnects, duplicate resolution and deployed Windows Server interoperability
remain open. No hosted Actions result is claimed for this working tree.

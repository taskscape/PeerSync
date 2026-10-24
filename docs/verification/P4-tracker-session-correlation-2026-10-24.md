# Tracker session correlation after the first-PC upgrade, 2026-10-24

The installed first PC's 1.0.19.0 journal recorded an initial tracker
announcement and HTTP/2 connection at 11:49:22 UTC, a completed refresh write
at 11:54:22 UTC, and `tracker.session_failed` at 11:54:42 UTC. The failure
reported `rpc_Unavailable`, Windows native socket error 10054 and
`elapsedMs=319306.3163`; a fresh initial announcement and connection succeeded
about one second later. This is stronger evidence than the older generic
`RpcException`: the stream was reset after the refresh write. It does not
identify whether the server, a firewall or another network intermediary
originated the reset. It also does not establish that the third PC's toast had
the same cause.

The current source records the server-issued tracker session ID as
`tracker.session_identified` and attaches that ID to later client failure and
disconnect events. The server records native socket errors and elapsed time on
tracker failures and elapsed time on closure. An intentional client shutdown
now always emits a disconnect event and does not report a false tracker
failure. The added diagnostic fields contain no device identity, folder ID,
endpoint address or exception message.

A real loopback mTLS stream verified that the client identification and
disconnect IDs match the server open and close IDs, and that intentional
client disposal emits no failure. Release Net passed 59/59; Release Rendezvous
passed 22/22. The first PC's installed 1.0.19.0 package predates this
correlation change, and the deployed server and third PC have not been
upgraded for it. A same-session client/server field capture is still needed to
attribute the reset and close P2.5.6 or physical WAN operation.

The first PC then repeated the same failure at 12:00:02, 12:05:23 and
12:10:46 UTC, each about 319 seconds after its preceding connection and about
19 seconds after its completed refresh write. A narrowly filtered Packet
Monitor capture covered the last reset and a separate HTTP/2 probe, but
recorded no packet events, so it cannot attribute the reset. The capture was
stopped and its filter removed. The third PC's `SocketException` remains a
separate, undiagnosed field report.

The current source now enables HTTP/2 keepalive PINGs after 60 seconds without
received frames, with a 30-second response deadline, for active tracker calls.
The rendezvous Kestrel host also pings idle HTTP/2 connections on that schedule.
This is a bounded mitigation for possible idle NAT/firewall expiry, not proof
that such an intermediary caused the 319-second reset. Reconnect remains the
fallback if a stream ends. Microsoft's [gRPC guidance](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance)
describes the required server cooperation and notes that pings do not prevent
all long-running-call termination; its [Kestrel guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options)
documents the server-side PING settings. A deployed stream surviving multiple
former failure intervals and a correlated server journal are still required
before the field issue is considered resolved.

The Release `PeerSync.Net.Tests` suite passed 59/59 and the Release
`PeerSync.Rendezvous.Tests` suite passed 22/22 with the keepalive configuration;
the TRX reports are in `artifacts/tests/http2-keepalive/`. These short local
tests establish that the mTLS tracker/relay flow still works. They do not
exercise a 319-second network path or prove that the deployed server responds
to PINGs, so the field check remains open.

Development-signed 1.0.21.0 x64 client and rendezvous installers were built
from committed source `42b52983390b1387893e7ce2c7d6fcad978cb4fe`.
The client installer is 166,884,512 bytes with SHA-256
`785C51B2F87562122C534AAA433BA20B02B88532085DDB99EE4EB9A410CE68C5`;
the rendezvous installer is 66,299,784 bytes with SHA-256
`8194A9382219AE2F8D431EF64305FB544684F149B3A88B6985A73BC52E894EBA`.
Both Authenticode signatures are Valid and their hashes match their manifests.
The installers are explicitly development-only, not release-signed.

The first PC upgraded from 1.0.19.0 to 1.0.21.0. The installer log records
successful commands and no Windows restart. The service is Running with
production sync available and four connected peer entries. The settings hash
and all three test text-file hashes match the pre-upgrade snapshot. The
installed `PeerSync.Service.exe` and `PeerSync.Net.dll` signatures are Valid and
their SHA-256 hashes match the just-published payloads. The before/after
records and setup log are under
`artifacts/field/client-keepalive-upgrade-20261024/`. A new tracker session
connected and logged its server-issued session ID. At 12:30:47 UTC, that
session had remained on the same established TCP connection for 11 minutes,
completed refreshes at 12:24:47 and 12:29:47 UTC, and logged zero tracker
failures or disconnects. Its exact bounded observation is retained as
`tracker-observation.json` beside the setup evidence. Four earlier 1.0.19.0
sessions had reset after roughly 319 seconds, so this is a field improvement
across two former failure windows. The server still runs the older version;
its new keepalive setting and enhanced failure journal have not been deployed.
The observation does not expose the PING frames or prove whether an idle
middlebox, server behavior or another cause produced the earlier resets.
Longer operation, same-session server logs and the third-PC bundle remain open.

Read-only access to the known second PC's installed service journal showed a
separate retry-accounting defect in its older 1.0.16.0 client. Its tracker
stream repeatedly failed after about five minutes, but the wait between the
12:28:26 UTC failure and 12:32:42 UTC reconnect was 256 seconds. The client
counted each long authenticated stream's later reset as another consecutive
startup failure, eventually reaching its maximum exponential delay. Current
source now clears that failure streak when the server issued a session ID and
the stream had been active for at least one minute. Short sessions that never
reach that state continue to back off. This change has not yet been deployed
to the second PC; the third PC's failure is still unclassified.

After the retry-accounting change, Release `PeerSync.Net.Tests` passed 59/59
and Release `PeerSync.Rendezvous.Tests` passed 22/22 without skips; TRX reports
are under `artifacts/tests/tracker-reconnect/`. These checks cover current
network and hosted tracker behavior, but do not induce a 256-second field
backoff. Installed second-PC recovery remains the decisive check.

The second PC later installed the development-signed 1.0.22.0 client built
from `c74a5d5`, including the retry-accounting change. Its service is Running,
its settings and pre-upgrade files are unchanged, and post-upgrade files
converged in both directions with matching hashes. A new tracker connection
and server-issued session ID appear in its journal. Because no reset has yet
been observed on this installed build, the reset/backoff behavior itself is
not claimed as field-verified. The first failed setup attempt, recovery and
successful retry are detailed in the
[physical two-PC record](P4-two-PC-cross-version-field-2026-10-24.md).

At 12:50:36 UTC, the second-PC 1.0.22.0 journal still showed its original
12:42:31 UTC server-issued tracker session ID, a completed 12:47:31 UTC
announcement refresh and no session failure or disconnect. The service was
Running. This eight-minute observation passes the older 319-second reset
window on that physical PC, but remains too short to establish long-term
stability or exercise the new backoff-reset branch.

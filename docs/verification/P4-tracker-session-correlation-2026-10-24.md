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

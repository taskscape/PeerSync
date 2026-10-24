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

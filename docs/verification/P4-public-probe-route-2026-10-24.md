# Production-name probe route, 2026-10-24

The installed first-PC CLI ran `psp probe sync.taskscape.com:99` and exited 0.
Its single correlated session `8a08a15c-ac15-43d1-a6ea-3c691f48da1d` reported
HTTP/2 over TCP and HTTP/3 over UDP reachable, with no transport errors. The
server returned observed address `192.168.0.1` and port 58097. This verifies
that both protocol legs and their correlation work from this PC to the
deployed service.

At the same time, DNS A resolved `sync.taskscape.com` to `45.11.63.194`; no
AAAA was returned. `Find-NetRoute -RemoteIPAddress 45.11.63.194` selected the
first PC's Ethernet address `192.168.0.176` and default next hop
`192.168.0.1`. The matching private observed address suggests local router
hairpinning, though the exact server-side path was not independently traced.
The operator then probed from the second PC. Its reported Ethernet source for
`45.11.63.194` was `192.168.0.183`, distinct from the first PC's current
`192.168.0.176`; `Find-NetRoute` displayed no `NextHop` value. The new probe
session `39cd7a4c-0aa9-4f98-939b-4130ec73b2e6` returned correlated HTTP/2
and HTTP/3 success and an observed address of `192.168.0.1` with port 60551.
This establishes that both installed PCs can probe the central name over both
protocols. Both PCs' private source addresses and matching observed address
are consistent with a shared local router, so this is not an independent
outside-network admission check. The pasted `status --json` block and first
probe match an earlier pre-join transcript; a fresh status is requested
separately before drawing a current peer-connection conclusion from that block.

These checks do **not** prove public provider admission from a separate
network. They also do not validate native NAT traversal, direct/relay path
selection, or the deferred mobile/CGNAT matrix. Keep P4.3.3/P4.3.4 and Gate
P4 open until a probe from a genuinely independent network is retained.

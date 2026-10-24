# P4 third-PC outside-network test kit — 2026-10-24

The existing two physical clients reached `sync.taskscape.com:99` through the
same local router, so their HTTP/2 and HTTP/3 probes do not establish an
independent outside-network route. A third Windows x64 PC on a mobile hotspot
or separate internet connection can provide that evidence without moving the
second PC off the verified LAN setup.

`build/Build-PeerSyncOutsidePcKit.ps1` produced the single transferable file
`artifacts/deploy/PeerSync-OutsidePC-1.0.16.0-x64.zip` (166,968,716 bytes,
SHA-256 `80429E678BACC1B55A0A8B81A21321EC1987CC153BD96CF961E66C277B9FE81B`).
It contains the signed x64 client setup used for the first two PCs, its public
development signing certificate and manifest, a read-only route/probe script,
and operator instructions. It has no server setup, private key, or folder key.

The client manifest identifies source revision
`24ae22395f1e43ebdcf5b16e50615513a6df47c2`. The signed setup's SHA-256
is `F7556B9EDF28C10ACA616B825416A893DC492278952C5B18529241D187C1A06D`;
the public certificate's SHA-256 is
`91B7B804D71E3D32FAF23FBA253D7D81EECDC54D1B22D158E2133F5D88D48578`.
The source setup had a valid Authenticode signature on the packaging PC.
The archive entry hashes matched the manifest, its five entries were inspected,
and the PowerShell probe parsed without errors. The development signer is for
isolated test PCs, not public release trust.

The remaining physical check is to install this client on the third PC,
disconnect its LAN path, run `Probe-OutsideNetwork.ps1` on the independent
connection, and retain the route, observed address, correlated HTTP/2 and
HTTP/3 results, and exit code. A successful probe closes only outside-network
rendezvous admission. A joined-folder transfer, direct/relay classification,
NAT variants, and the 72-hour WAN soak still require separate field evidence.

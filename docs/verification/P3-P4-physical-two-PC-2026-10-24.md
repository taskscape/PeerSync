# Two-PC client field check, 2026-10-24

This is partial physical two-PC evidence. It does not close the 1 GiB LAN,
native NAT, relay, long-soak, or release gates in `plan.md`. A live direct LAN
QUIC folder session is identified below, but the exact route of the earlier
file transfers is not. The second PC's OS build and sanitized deployment
bundle have not been collected.

The first PC reports Windows 11 Pro build 26300 on an ASUS system with a
1 Gbps Realtek Ethernet adapter. Its route to the second PC's supplied
`100.88.55.83` overlay address uses the Tailscale interface. That route
lookup does not prove which PeerSync candidate carried a given session, and
it cannot establish a native LAN or NAT result.

The second PC installed the development-signed 1.0.16 x64 client. The operator
reported a running `PeerSync` service. Its installed CLI reported no peers
before folder enrollment and reached `sync.taskscape.com:99` with correlated
HTTP/2 over TCP and HTTP/3 over UDP. A successful probe proves central-server
reachability, not file synchronization.

On the first PC, a disposable folder at
`C:\PeerSync-Test\Physical-Two-PC-20261024` was granted to the service account
and registered with read/write access. Its capability was copied to the local
clipboard for the operator to paste into Join on the second PC. The capability
was not printed into the evidence. The folder was not paused. The first PC's
`psp peers` then listed one pinned second-PC identity, and `psp status` reported
connected sessions with an accepted clock. The first PC's service logs recorded
the new folder's peer index ready and index received events.

Before upgrading the first PC, its installed binaries reported version 1.0.13.
The second PC created `second-pc-check.txt`; the first PC received it as a
three-byte file with SHA-256
`F1B2F662800122BED0FF255693DF89C4487FBDCF453D3524A42D4EC20C3D9C04`.
This confirms a file transfer from the second PC to the first across those
versions. The first PC had already indexed `first-pc-check.txt` (82 bytes,
SHA-256 `979EF36BBE2399AEEC99C226BB8F12C9FEF41C929A2EE5F090B07A91E69903B4`),
but no second-PC hash has been provided for that file.

The first PC was then upgraded in place with the signed 1.0.16 x64 setup whose
SHA-256 is `F7556B9EDF28C10ACA616B825416A893DC492278952C5B18529241D187C1A06D`.
Setup returned exit code zero. The service restarted under
`NT SERVICE\PeerSync`; the installed service binary reports
`1.0.16+24ae22395f1e43ebdcf5b16e50615513a6df47c2` and a valid
development Authenticode signature. The registered test folder, both files,
and their local hashes remained intact. The second-PC peer reconnected with an
accepted clock. The packaged Client field collector exited zero with no issues:
it found service-owned TCP and UDP listeners, valid local crash policy, and
correlated HTTP/2 and HTTP/3 central probes. Its sanitized evidence is in
`artifacts/field/physical-two-pc-20261024/first-pc/` (ignored by Git), with
`deployment-evidence.json` SHA-256
`BDC78B2004271695CD3C213209CAAC61C0A9133FCF13D8681267BBEB0AC1A2B1` and
`client-diagnostics.zip` SHA-256
`CF533A73D81BCBD0C64E7EF282ECBE86929CA3332FB1A5B5085B5F2F2CCAA1E5`.

For the matching-version forward check, the first PC created and indexed
`post-upgrade-first-pc.txt` (85 bytes, SHA-256
`1DA04139ED14F8290E4453DA841F42E46F3E38F580007C3D7B4597EB5878ACEB`).
The operator reported that this file arrived on the second PC and that its
SHA-256 matched. The second-PC hash command output was not retained in this
record, so this is operator-reported evidence rather than an independently
collected remote artifact. Together with the three-byte reverse transfer, it
demonstrates basic two-way synchronization after separately installing the
clients. It does not establish the P3 unassisted, app-only, under-ten-minute
screen-recorded gate, a native direct or relay route, or the P2/P4 physical
network and soak requirements.

A fresh second-PC `psp status --json` later reported the same installation
instance `c0223abf-fc92-4bbb-b23c-8f7135514fd7` at sequence 9703, with
`productionSyncAvailable=true`, two connected rows for the first-PC device,
and `clockState=Accepted` on both. An earlier sequence-99 status with no peers
was a pre-join snapshot. The first PC also reported connected, accepted rows
in a separate local check. Those snapshots were taken at different times and
their session IDs differ, so they are not a correlated same-session capture.
The selected LAN/public/relay candidate is not recorded in the installed
version's sanitized status output. New `connection.candidate_selected` and
`connection.incoming_selected` diagnostic events will identify the winning
outgoing or incoming session, transport and actual remote endpoint after a
build containing them is deployed. Those events have not been observed on
either installed 1.0.16 client, so the exact route of the earlier two-way file
transfer remains unverified. The newly built source CLI queried the installed
service and reported `transport: Quic` for the enrolled second-PC peer; the
installed 1.0.16 CLI omitted this already available wire field.

A later first-PC LAN preflight at 2026-10-24 09:36:28 UTC found a direct
on-link Ethernet route from `192.168.0.176` to the second PC's
`192.168.0.183`, a reachable neighbor entry and a 1 Gbps local NIC. The
first-PC service journal also recorded LAN discovery of `192.168.0.183`.
These observations establish that a physical LAN candidate is available;
they do not identify the selected path of either active PeerSync session or
measure the required 1 GiB throughput. The bounded raw preflight is retained
at `artifacts/field/p0-quic-lan-20261024/first-pc-preflight.json`.

The first-PC raw service journal also records an authenticated QUIC folder
session `4d2ef52b-0bb0-1871-7546-0e6e55f29519` with actual remote socket
address `192.168.0.183` at 08:50:12 UTC. That same session opened the folder
stream, became peer-ready and ran folder sync passes. Combined with the
on-link Ethernet route, it confirms a live native LAN QUIC session between
the physical PCs. This was observed before the source selected-route events
were deployed, and the journal does not correlate the previously reported
file's blocks to this specific session. A separate 1 GiB mutual-certificate
QUIC throughput run subsequently passed on this same physical LAN; see
[the correlated spike evidence](P0-physical-lan-quic-2026-10-24.md). That run
does not identify the session used by the earlier product file transfers.

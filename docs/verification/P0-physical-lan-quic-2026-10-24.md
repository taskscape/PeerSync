# P0.2.1 physical LAN QUIC transfer, 2026-10-24

Two physical PCs on the same Ethernet LAN completed a 1 GiB QuicBasics
transfer. The first PC selected its on-link `Ethernet` route from
`192.168.0.176` to `192.168.0.183` with no next hop and a reachable neighbor;
both PCs reported 1 Gbps Ethernet link speed. This is native LAN traffic, not
the earlier Tailscale overlay route. It is a QUIC spike result, not a 50 GB
three-PC service sync or a 72-hour soak.

| Observation | Result |
| --- | --- |
| Sender terminal status | Passed, `verified-quic-transfer`, 1,073,741,824 bytes |
| Shared transfer session | `ea35790a-166e-40a8-8027-46efa4f3317c` |
| Endpoints | `192.168.0.176:51397` to `192.168.0.183:47821` |
| Sender time and rate | 10.9818278 s; 93.24495144606074 MiB/s (about 782 decimal Mbit/s) |
| Protocol and hash | ALPN `psp/1`; sender reports `verified: true` after matching the receiver's SHA-256 digest in constant time |
| Sender local DeviceId | `4C7F81DA2DE06DA0B9860C45750D063C081564E196E1E38AB5B1855C0AD55B1D` |
| Sender observed remote DeviceId | `CFD120E5A855E006AC8C0FCDB2CAAAA3C33E0B1CBD8989CCFD161C7754537C75` |
| Receiver journal | Same session ID, `transport.authenticated` from `192.168.0.176`, `transfer.completed`, 1,073,741,824 bytes in 10,906.476 ms |
| Receiver console | Same session ID; 1,073,741,824 bytes in 10.906476 s at 93.88917190117138 MiB/s; ALPN `psp/1`; `verified: true` |
| Receiver identities and endpoints | Local `CFD120E5A855E006AC8C0FCDB2CAAAA3C33E0B1CBD8989CCFD161C7754537C75`, remote `4C7F81DA2DE06DA0B9860C45750D063C081564E196E1E38AB5B1855C0AD55B1D`; `192.168.0.183:47821` to `192.168.0.176:51397` |

The spike requires client and server TLS certificates, checks the negotiated
ALPN, and derives both DeviceIds from `RemoteCertificate`, rather than an
untrusted message field. The listener creates a fresh nonexportable P-256 CNG
key and self-signed certificate; no certificate import was required on either
PC. The operator supplied the receiver's final console JSON. Its local and
remote DeviceIds exactly reverse the sender's, and both endpoint pairs,
session IDs, byte counts and `verified` results agree. The matching receiver
journal independently corroborates transfer completion.

The receiver did not initially have the assumed `C:\Temp\PeerSync` spike
payload. A source-built, development-signed single-file executable (SHA-256
`3264C1424A38343C390894D1721FDEBE904E628BF058CB84E29A5CE8A1C730D8`)
was delivered through the already joined PeerSync folder. It launched but
reported `PlatformNotSupportedException` at `listen`: the optional native
MsQuic library was absent beside the single-file executable. The same failure
was reproduced locally. Placing Microsoft's validly signed `msquic.dll`
(SHA-256 `7B32A336B638570BFF7B2F4782EBDC05B8C70FAC76FDB412F3E8A3C181C32A61`)
beside it made a local listener print `READY`, and the second-PC listener then
printed `READY 47821`. The sender's prior dial timeouts and the earlier
missing-path/optional-library failures are preparation errors, not failed
authenticated transfers. The installed multi-file client/spike payload
includes `msquic.dll` already.

The first-PC ignored field directory
`artifacts/field/p0-quic-lan-20261024/` retains the helper's `terminal.json`,
per-attempt stdout/stderr, the first-PC route preflight and copied sender and
receiver `debug.jsonl` files and the operator-supplied final receiver JSON.
An independent comparison of sender, receiver and journal fields passed all
six checks: session, bytes, reciprocal IDs, reciprocal endpoints, ALPN and
both verification flags. The terminal file's SHA-256 is
`120A57EEAF333AA696AB745B367D8FCCF5361048F9E86F19FD80E855230D1B7E`;
the sender journal's SHA-256 is
`A593C7B36E32DD3A0FE320EC13884D9510DB303295E9ACF589F6EED686F3B719`;
the receiver journal's SHA-256 is
`2AFA89C5FE3DB6BC357219A098F111A453E40E0A6EB5F44508A3F07412F008C4`.
The receiver JSON's SHA-256 is
`DA3426A84A27C0C614F2B9138483A1B77D987C7C9C5B968D5A0192A01848FD51`.
The receiver journal originally arrived in the joined folder through PeerSync.
No folder capability or private key is included in this report.

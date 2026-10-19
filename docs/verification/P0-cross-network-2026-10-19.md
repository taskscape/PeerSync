# P0 cross-network evidence — 2026-10-19

Date is Europe/Warsaw. UTC event timestamps below fall on 2026-10-18.
User-provided remote console results and an inspected local receiver journal
establish the scope below. This is not a production service deployment or a
physical LAN/NAT-traversal benchmark.

## Two physical PCs: 1 GiB over Tailscale

The user states these PCs are on different LANs. The remote sender dialled
`100.118.160.63:47821` from `100.126.96.2:49950` using the refreshed QuicBasics
probe, with 1073741824 bytes and `--timeout-seconds 1800` on both peers.

| Evidence | Result |
| --- | --- |
| Shared session | `453ac261-6af6-4485-b249-fea17cd6ee98` |
| Sender console | 1,073,741,824 bytes; 220.7767444 s; 4.6381696712799245 MiB/s; `verified: true`; ALPN `psp/1` |
| Receiver journal | Same session; `transfer.completed`; 1,073,741,824 bytes; 220526.4611 ms |
| Sender DeviceId | `A34AC90A8EFF5609D9D06AAAC040E63F6759D2CCCAEB630FD2E132A2BF2D96A3` |
| Receiver DeviceId reported by sender | `4B86799D3532E30FCD9FA890974A03776A5D77F754571016E50C355699B21660` |
| Receiver source | `C:\Temp\PeerSync\logs\41076\debug.jsonl`, inspected directly |
| Retained local bundle | `artifacts/evidence/2026-10-19/quic-1gib-receiver.zip`, collected with the current collector |

The receiver started listening at 23:40:02 UTC and authenticated at 23:42:04
UTC, after waiting about 122 seconds. Its transfer completed at 23:45:44 UTC.
This run validates the independent waiting/transfer deadlines on the real
two-PC path: waiting plus transfer exceeded the old shared 180-second limit.
It does not establish the exact cause of every earlier failed run.

The successful probe requires both TLS certificates, derives identities from
the certificates, and checks the receiver's SHA-256 against the sender's data.
No manual TLS certificate installation was needed. Throughput was about
38.9 decimal Mbit/s on this overlay path. Tailscale's route type throughout the
transfer was not recorded; this is not a measured native direct-Internet or
PeerSync-relay transfer. The remote sender's structured log bundle and the
receiver's final console JSON were not supplied for this report.

## Public Windows Server probe: TCP/UDP 99

The user ran `Rendezvous99.exe server 99` on the Windows Server at
`45.11.63.194`. Reported log directory:
`C:\Users\admin\AppData\Local\PeerSync\spikes\Rendezvous99\86c9e185fc454eb29751431453105350`.

| Requested protocol | Session | User-provided result |
| --- | --- | --- |
| HTTP/2, TCP 99 | `1713167250da4c018cd0b3b0d391959c` | `mutualTls: true`, `deviceIdentityVerified: true`, 3 bidirectional messages |
| HTTP/3, UDP 99 | `32cc39bd210342aa86f7c43878be5218` | `mutualTls: true`, `deviceIdentityVerified: true`, 3 bidirectional messages |

An initial HTTP/2 request cancelled after the probe deadline; independent local
TCP connection tests timed out at 5 and 15 seconds. After the user opened TCP
99 in the server firewall, HTTP/2 passed. The subsequently reported HTTP/3
test also passed. These exact-version clients do not silently fall back.
Server structured logs, exact Windows build and hosting-provider configuration
were not inspected in this report. The results establish public probe
reachability and mutual-certificate streaming, not P4 product readiness.

## Plan impact

P0.2.1 now has successful physical two-PC, 1 GiB mutual-TLS evidence over an
overlay. Its required 1-Gbit physical LAN throughput remains unverified.
P0.2.2's native NAT matrix and final transport decision remain open: Tailscale
handles connectivity for the successful overlay run. P0.2.6 gains external
public-endpoint evidence for both protocols. Gate P0 remains open.

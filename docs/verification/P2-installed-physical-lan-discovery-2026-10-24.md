# Installed physical LAN discovery — 2026-10-24

The first physical PC's installed PeerSync 1.0.18.0 service started a fresh journal run at 2026-10-24 10:53:49.5293449 UTC. The journal recorded `discovery.lan_started` at +0.179 s, `discovery.lan_peer_found` for the second PC's previously confirmed same-LAN address at +1.025 s, and `connection.candidate_selected` with operation `candidate.lan.quic` to that same address at +1.577 s. The selected candidate identifies actual direct LAN QUIC, not merely a received discovery packet. The source is the live installed service journal under `C:\ProgramData\PeerSync\logs\service\`; raw network addresses remain subject to its 24-hour retention policy. The second PC's LAN address was independently reported in the [physical QUIC check](P0-physical-lan-quic-2026-10-24.md).

This establishes first-PC detection and selected direct transport within the plan's five-second threshold on the two physical Ethernet PCs. That initial check did not show the second PC's reciprocal discovery timestamp, each required multicast/broadcast path on every active adapter, or the Server adapter behavior.

The second PC's later installed 1.0.22.0 service run supplies reciprocal
evidence. Its journal recorded `discovery.lan_started` at 12:42:31.4607271 UTC
and `discovery.lan_peer_found` for the first PC's IPv4 and IPv6 link-local
addresses at 12:42:32.3623 UTC, 0.902 seconds later. It selected an
authenticated QUIC candidate to the first PC's LAN IPv4 address at
12:42:35.7352 UTC, 4.274 seconds after discovery start. That candidate's
operation label was `candidate.public.quic` because the address was already
in the tracker catalog; its actual destination was the verified LAN address.
This adds the reciprocal physical-PC timing. The logs do not prove that every
active interface's multicast and broadcast path worked, nor the separate
Server adapter behavior, so P2.3.1 remains unchecked.

After both physical PCs upgraded to development-signed 1.0.23.0, the first
PC's installed service selected an authenticated QUIC session to the second
PC's `192.168.0.183` address at 13:18:55 UTC. `Find-NetRoute` selected source
`192.168.0.176` on the Ethernet adapter with no next hop. That session's
correlated service journal later recorded a successful folder sync pass with
one applied item and zero deferred items. The [two-PC field check](P4-two-PC-cross-version-field-2026-10-24.md)
also verified one new text file in each direction with matching native
SHA-256. These observations establish a current-build physical LAN QUIC
session and native byte convergence; they do not prove every adapter's
multicast/broadcast path, the separately required three-PC corpus (later
revised to 9 GiB), or native WAN NAT traversal.

## 2026-10-28 live three-PC multicast membership snapshot

During the scheduled 09:00–12:00 UTC physical LAN soak, a read-only Windows
snapshot checked all three installed PeerSync services without restarting a
service, changing an adapter or touching a synchronized folder. At about
09:22–09:23 UTC, each service was Running and owned both IPv4 and IPv6 UDP
47820 listener sockets. `netsh interface ipv4/ipv6 show joins` showed the
required `239.255.80.83` and `ff12::5053` groups on every currently Up
non-loopback adapter:

| Physical PC | Up adapters | IPv4 group joins | IPv6 group joins | UDP 47820 owner |
| --- | --- | ---: | ---: | ---: |
| R5-7600X | Ethernet, Tailscale, Default Switch, WSL vEthernet | 4 | 4 | service PID 28100 |
| DESKTOP-BN535U0 | Ethernet, Wi-Fi, Tailscale, WSL vEthernet | 4 | 4 | service PID 23888 |
| LARKBOX-X-1 | Ethernet, Tailscale | 2 | 2 | service PID 14948 |

The first and second installed service journals independently record
`discovery.lan_memberships` for four IPv4 and four IPv6 interfaces in their
current process runs. The ignored raw snapshots are
`artifacts/field/lan-memberships-20261028/{first,second,third}.json`, with
SHA-256 values respectively
`E551ED4FD934474BB706E7AA2D48B311D10D087F485AFB44360E6CCE75FE8249`,
`6090D866C428B63686C1ECC4A561E4E8ECAC0A2B15D89110C0FCC89D54F2E562`,
and `D9F868F7AE1F97E6332D00147EA627389FB51E6B0AEAD95F1686446AA4503F23`.
The third-PC remoting credential remained encrypted locally and was not
included in any snapshot.

This establishes Windows group membership at one point in time. It does not
show outbound multicast or subnet-broadcast packets on every adapter, a
post-switch rejoin, or Windows Server client adapter behavior. Those checks
and the complete P2.3.1 acceptance therefore remain open.

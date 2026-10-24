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

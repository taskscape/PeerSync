# P4 third-PC mobile-hotspot rendezvous probe — 2026-10-24

The operator transferred the signed development client kit to a third Windows
x64 PC and ran its read-only `Probe-OutsideNetwork.ps1` while that PC was on a
mobile hotspot, separate from the first two clients' Ethernet LAN. The active
physical adapter was Intel Wi-Fi 6E AX211 at 144.4 Mbps. The DNS A record for
`sync.taskscape.com` resolved to `45.11.63.194`; `Find-NetRoute` selected
Wi-Fi address `10.133.234.210` for that server. The rendezvous server reported
public observed address `37.248.209.217` and port 9293, unlike the first two
clients' shared-router observation of `192.168.0.1`.

One correlated probe session, `2e78bfd1-0313-4176-907d-0327f6d56e2b`,
reported `tcp.reachable=true` over HTTP/2 and `udp.reachable=true` over
HTTP/3, both `correlated=true`, with no reported transport error. The probe
exited 0. The installed third-PC client's status reported a live instance,
sequence 1, `productionSyncAvailable=false`, and no peers. That is expected
before it joins a folder.

Together with the [installed server's local health, listener and firewall
checks](P5-rendezvous-server-deployment-2026-10-24.md), this closes P4.3.3,
P4.3.4 and Gate P4's independent UDP-99 reachability item. It proves
rendezvous admission from an outside route, but does not yet prove a shared
folder transfer, native hole punching, whether a direct or relay path wins,
mobile CGNAT classification, or a WAN soak. Those remain open for the third-PC
field run.

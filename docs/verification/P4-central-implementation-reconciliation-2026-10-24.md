# P4 central implementation acceptance — 2026-10-24

Four P4.1 implementation items had remained unchecked after the server code
merged. The current `RendezvousHost` registers the `PeerSyncRendezvous`
Windows service, binds port 99 with HTTP/1, HTTP/2 and HTTP/3, and selects
TLS 1.3 only. `RendezvousCertificateLoader` selects a current server-auth
certificate with private key from `LocalMachine\My`. Public health, bootstrap
and probe requests share the listener and may omit a client certificate;
`TrackerService` and `RelayService` call `RendezvousRpcIdentity.RequireAsync`
and require a valid device certificate before granting RPC access. That
method derives the DeviceId from the certificate, not from an announcement.

`TrackerService` sends `ObservedAddress` to its certificate-bound session;
`PresenceRegistry` publishes `PeerList`, limits each announcement to 500 folders, broadcasts
only to peers sharing a FolderId, and expires announced sessions after 15
minutes. Clock-controlled registry tests cover folder isolation, stale
presence removal and peer-list updates; the host test covers the observed
address and certificate requirement. The current-source focused
`PeerSync.Rendezvous.Tests` suite passed 17/17 with no skips on 2026-10-24.
The [installed two-guest tracker run](P4-two-guest-central-relay-2026-10-23.md)
also exchanged peer lists and synchronized a file through the central
service, though it used a virtual switch.

The tracker dispatches `PunchRequest` through `PresenceRegistry.RequestPunch`.
Its clock-controlled test rejects an unrelated FolderId, then confirms both
participants receive `PunchNow` with each other's certificate-derived DeviceId
and a shared start time exactly 500 ms ahead. This closes P4.1.3's server
coordination behavior. It is not evidence that same-port UDP punching works on
physical NATs; the native matrix and final transport decision remain open.

The hosted test now also reads `/.well-known/peersync.json` without a client
certificate and checks its schema and tracker/relay origins. It captures live
`peersync.probe.requests`, `peersync.tracker.sessions.opened` and
`peersync.authentication.rejected` measurements with a `MeterListener`. After
announcing a random 32-byte FolderId, it checks that neither its hex nor
base64 representation appears in the raw JSONL journal or sanitized ZIP.
The current-source focused suite passed 17/17 with no skips; TRX evidence is
under `artifacts/tests/p4-central-observability/`. These are hosted checks.
The installed service's OTLP delivery and Windows Event Log entry still need
deployed verification, so P4.1.5 remains open.

The [Server 2025 deployment](P5-rendezvous-server-deployment-2026-10-24.md)
installed version 1.0.17.0 under `NT SERVICE\PeerSyncRendezvous`, confirmed
service-owned TCP and UDP 99 sockets and both inbound firewall rules, and
passed the packaged TLS-validated local health check. An earlier development
package [upgraded in place and passed two uninstall/reinstall cycles](P3-server2022-uninstall-cycle-2026-10-23.md)
on a separate Server 2025 guest. The [independent third-PC probe](P4-mobile-hotspot-probe-2026-10-24.md)
subsequently reached the deployed server through HTTP/2 and HTTP/3 from a
mobile hotspot. Together these close P4.1.1, P4.1.2, P4.1.3 and P4.1.6 as
implementation items.

P4.1.4/5/7 and P4.2 still retain their own acceptance criteria, including
native NAT choice, relay capacity/diagnostics and physical WAN results.
Development code signing is not a public release signature; Gate P4 and Gate
P5 remain open.

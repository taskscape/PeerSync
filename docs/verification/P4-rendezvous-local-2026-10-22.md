# P4 rendezvous local and clean-VM validation

This report covers the working-tree P4 implementation before physical WAN
testing. The Server 2025 guest is a Hyper-V VM with a loopback probe and a
self-signed test Server Authentication certificate trusted only inside that
guest. It does not prove public DNS, provider firewall, native NAT traversal,
cross-network relay, load caps at scale, or Gate P4.

## Code and test checks

- Locked solution restore and Release build passed with zero warnings/errors.
- `PeerSync.Rendezvous.Tests`: 9/9 passed, including HTTP/2 and HTTP/3,
  anonymous gRPC rejection, certificate-bound tracker access, presence folder
  isolation and 15-minute expiry, 500 ms punch introductions, relay token
  binding/caps, and an end-to-end TLS-over-relay protocol exchange.
- `PeerSync.Net.Tests`: 51/51 passed; `PeerSync.Service.Tests`: 37/37 passed.
- `PeerSync.App.Tests` excluding the three desktop-render methods: 15/15
  passed, including control-snapshot mapping of Relay to `via relay` and QUIC
  to `direct QUIC`.
- The full local App suite ran 17 methods: 14 passed and three existing
  `ClockStatusRenderTests` failed because the off-screen WPF capture was an
  entirely black 1100x480 image despite the window reporting visible/loaded.
  This is a local rendered-UI validation gap, not evidence of a clock or relay
  behavior failure. The peer Route column still needs a rendered check on an
  interactive desktop and hosted CI results.

## Signed Server 2025 upgrade

The signed development `PeerSync-Rendezvous-Setup-1.0.3.0-x64.exe` was built
with a locally trusted non-exportable test signing identity. PowerShell
Authenticode reported `Valid` and an RFC3161 timestamp. On a clean Windows
Server 2025 evaluation VM, it upgraded the earlier 1.0.0 test installation
in place: setup exit 0, service `Running`, installed file version `1.0.3.0`.
The `PeerSyncRendezvous` Application Event Log source exists. TCP and UDP 99
listeners were confirmed in the earlier 1.0.0 installation and the post-upgrade
probe exercised both current listeners.

Inside that VM, the installed CLI's `psp probe sync.taskscape.com:99` used a
test-only hosts entry to reach its own listener. It returned exit 0,
`tcp.reachable=true` at HTTP/2, `udp.reachable=true` at HTTP/3, and observed
address `127.0.0.1`. The server journal recorded startup plus separate HTTP/2
and HTTP/3 probe events. From the Hyper-V host, an anonymous HTTP/2 gRPC
session request to the guest was rejected. The server logged
`rendezvous.auth_rejected` and its installed Application Event Log source
emitted warning event 1001 with the same safe code. The host request used
test-only certificate validation because the guest's self-signed server
certificate was not trusted on the host; this does not prove public CA
validation.

`PeerSync.Rendezvous.exe diagnostics --output` created a sanitized ZIP with
five recent events and an environment manifest. The bundle contained the
authentication rejection but no `remoteAddress` or `folderId` fields. A
service-start information event was also present in Application Event Log.

The current physical requirements remain open: public certificate via
win-acme, outside-network TCP and UDP 99 probes, two real PCs on different
NATs and mobile/CGNAT, forced relay sync and rate-cap load, 72-hour WAN soak,
and the deferred P0.2.2 NAT matrix/final transport decision.

# P4 public rendezvous endpoint preflight, 2026-10-23

A read-only outside-network check from the development Windows 11 host
resolved `sync.taskscape.com` to `45.11.63.194`, the previously supplied
central-server address. `Test-NetConnection 45.11.63.194 -Port 99` returned
false after a TCP timeout. The locally built `psp probe sync.taskscape.com:99`
exited 1 with session `93746e24-c13d-4193-90e8-7f270de31a15`:

| Leg | Reachable | Observed HTTP version | Category | Cause |
| --- | --- | --- | --- | --- |
| TCP / HTTP/2 | no | none | timeout | `TimeoutException` |
| UDP / HTTP/3 | no | none | timeout | `TimeoutException` |

Neither leg returned an observed address. The probe cannot distinguish an
offline service, local or provider firewall, or an unrouted endpoint from
these timeouts alone. No claim is made about the currently deployed binary or
certificate. On the server, check the `PeerSyncRendezvous` service state,
its TCP and UDP listeners on 99, firewall rules, and the correlated server
journal; then repeat this outside-network probe. The P4.3.3 and P4.3.4 gates
remain open.

# Physical two-PC forced relay, 2026-10-25

Two physical Windows clients running signed PeerSync 1.0.25.0 were kept on
their 1 Gbps LAN while a scoped field check blocked PeerSync's direct TCP
and UDP listener ports. Each PC's inbound listener port and outbound ports
for the other physical peers were blocked for five minutes. Central TCP 99
remained available. The first and second service journals selected the same
authenticated `Relay` session
`2d3bae49-d3e8-a5fb-47b1-d84f2ed5b97f` in outgoing and incoming
directions. No direct candidate was selected for that shared session.

With both sets of rules present, the first PC created 64 MiB of random file
content. The second PC completed fifteen `Relay` block requests totaling
exactly 67,108,864 bytes on the shared session, installed the native file,
and returned SHA-256
`4870A1A6874A280D499E78980BF9BDF8ABE942C75DCE1F9B735A3AEE2BCB1AF2`,
matching the source. A second 32 MiB random file seeded on the second PC
arrived on the first. Its seven `Relay` block completions totaled exactly
33,554,432 bytes on the **same** session; both native hashes were
`5741752C53D07CE8C4CC150951FB3339E27D33DD0811FC13AD21275E1178B387`.
The first-PC installed CLI also displayed `Relay` for the second device
during the firewall window. Because the PC clocks differ by about 40 seconds,
the shared session ID, per-receiver byte totals and native hashes carry the
correlation; cross-PC timestamps alone do not.

Both five-minute reports recorded all four scoped rules still present and
both services Running at the end of their measurement windows. Both then
recorded rule removal, recovery-task removal, service restart and a Running
service with no failure. The second PC's one-time launch task was removed.
After cleanup, the installed CLI showed a QUIC route again; a fresh file
arrived directly on the second PC in 5.18 seconds with matching SHA-256
`E881290DAB75A564F347C731338C3E2F1CC77C09C4B2E674EE041D2F7FFDD4E2`.
The public Rendezvous probe again passed correlated HTTP/2/TCP and
HTTP/3/UDP 99. The allowlisted summary and local test reports are retained
under `artifacts/field/forced-relay-physical-20261025/` (ignored by Git);
the raw journals remain on each client.

After the run, each installed client produced a sanitized diagnostic bundle.
The first and second bundles contain the shared Relay session ID 78 and 86
times respectively, so an operator can correlate the two client accounts
without their raw journals. A targeted screen of both ZIP entries found no
occurrence of the current folder share key, folder ID encodings, native test
paths or names, or the known LAN, overlay and public addresses. The screen
result and bundle SHA-256 hashes are retained in
`artifacts/field/forced-relay-physical-20261025/diagnostic-bundle-screen.json`.
This checks those known values in these two bundles; it is not a general
secret-leak audit. A matching central-server bundle is still needed for the
three-way diagnostic gate.

The first attempt at starting the second-PC field tool failed **before** its
measurement window: the new harness read undefined `$LASTEXITCODE` after
the installed PowerShell stop helper. It removed the test rules but left
that service Stopped. The service was started remotely, verified Running,
and a new file synchronized with matching SHA-256. The harness now requires
the helper's `STOPPED_AND_EXITED` marker and retains the independent recovery
task until both rule removal and a Running service are confirmed. The
initial failure report is retained beside the successful reports; it is not
counted as a passing relay run.

The receiver-side block-completion spans were about 56 seconds for 64 MiB
and 18 seconds for 32 MiB. These observed rates are below 2 MB/s, but they
do not prove the per-device limiter caused the rate or establish the total
cap relative to the provider uplink. This check used two PCs on one LAN and
did not inspect a matching central-server bundle, render the dashboard's
`via relay` field, classify a native NAT, or run for 72 hours. Those Gate P4
and release checks remain open.

# Correlated physical LAN route, 2026-10-25

Both physical Windows clients were running the signed 1.0.25.0 service. The
first PC's current service journal selected an outgoing QUIC candidate to
`192.168.0.183:26328` with authenticated session ID
`4eacd890-7539-1e88-d08d-d250994bcc0a`. The second PC's current journal
selected the incoming QUIC peer from `192.168.0.176:61622` with the **same**
session ID. The first PC's route to `192.168.0.183` used its up 1 Gbps
Ethernet adapter, source `192.168.0.176`, with no next hop. These are actual
selected socket endpoints, not just discovery or advertised candidates.

A new 67-byte test file written on the first PC arrived in the second PC's
native joined folder in 3.900 seconds. Both machines returned SHA-256
`D8D2C35264D083D71F84D0DACD8C943B243A41F4CD07DAFFD35246E13D00CDD1`.
The second journal recorded a 67-byte `transport.request_complete` and a
`service.folder_sync_pass` with one changed item on that same QUIC session.
The first journal records folder sync and index activity on it. This ties
native file bytes to a selected on-link physical LAN connection across both
clients. The clocks differ by roughly 40 seconds, so the shared authenticated
session ID and native hash, rather than wall-clock alignment alone, establish
the correlation.

The first-PC test command had a non-fatal PowerShell parameter-binding error
in its uniqueness preflight. It then created the uniquely named file, waited
for the second-PC copy and verified both native hashes. The read-only
allowlisted field report is retained at
`artifacts/field/selected-route-1.0.25-20261025/report.json` (ignored by Git).
The source and destination service journals remain on their respective PCs.

This closes the installed two-PC selected-route diagnostic check. It is a
single small-file LAN result; it does not establish the P2 80% line-rate
target, 50 GiB/100,000-file three-PC corpus, 72-hour soak, native NAT matrix
or WAN relay load cap.

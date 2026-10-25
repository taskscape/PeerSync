# Bounded physical forced-relay tool, 2026-10-25

`build/field/Test-ForcedRelayPeer.ps1` prepares a physical relay-only check
without disabling a network adapter or changing central TCP port 99. On each
client it validates the installed Running PeerSync service, checks its TCP
and UDP listener ownership, and requires the local listener port plus every
remote peer listener port that could provide a direct path. During a bounded
interval it blocks only that service executable's inbound TCP/UDP listener
port and outbound TCP/UDP connections to those listed peer ports. An
independent SYSTEM scheduled task removes those exact rules and restarts the
service if the invoking shell ends. The normal path also removes the rules,
unregisters the task and restarts through the installed process-exit helper.
It records start/end, rule and service cleanup status in a local JSON report.

The source SHA-256 is
`D1F53A0071638B08AC8D0665F3EC7E30B3CEEE9156ADFC2087594F56EE5B7247`.
Windows PowerShell 5.1 parsed the tool and the field-kit builder. An initial
external-shell call exposed that comma-separated peer ports reached an
`int[]` parameter as one string; the tool now parses and validates both
comma-separated and array arguments. The actual Windows PowerShell 5.1
read-only `-PreflightOnly` path passed on the physical first PC running
1.0.25.0 with listener port 45973 and candidate remote ports 26328 and
38240. It left no relay-check rule or task. A separate disabled firewall
rule control accepted the two remote ports and reported them in the port
filter, then was removed. These controls do not exercise the mutating
firewall/service path.

The next physical check must run the tool on **each** participating PC with
the other peers' current listener ports, wait for every `READY`, transfer a
fresh file, and require matching native hashes plus authenticated `Relay`
selected-session evidence in the client and central journals while the rules
are present. Inspect `CLEANED` and the JSON reports afterward. If any
direct path remains, either service fails, a recovery task fires, or any
report is incomplete, the run is not forced-relay evidence. It also cannot
by itself establish the two-home NAT matrix, provider uplink cap or 72-hour
WAN durability.

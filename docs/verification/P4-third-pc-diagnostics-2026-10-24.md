# Third-PC mobile-network diagnostic bundle — 2026-10-24

The operator ran the 1.0.23.0 field kit's collector on the third Windows PC
while it was on a mobile hotspot and transferred
`PeerSync-diagnostics-20261024-191035.zip` by Tailscale Taildrop. The copy
retained under `artifacts/field/third-pc-diagnostics-20261024/` is 1,699,337
bytes with SHA-256
`DBBB15865159699F6D7C0DC7D3EEA2B746424E3B363F00067B4257224D46F37F`.
The bundle metadata says Windows 10.0.26300, .NET 10.0.13, x64, five log
files, and `networkAddressesIncluded=false`. The collector kept 100,000
events and omitted 57,580 older events under its bound. Retained events span
10:38:30–19:10:35 UTC; conclusions below apply to that window. The bundle
does not include an installed executable version or app-rendering evidence.

The earlier service run contains 24 `connection.candidate_failed` warnings
with `SocketException` on public TCP candidates. Its adjacent
`transport.tcp_connect_failed` events include native `10049`
(`socket_AddressNotAvailable`, 19 events) and `10051`
(`socket_NetworkUnreachable`, five events), plus 66 cancelled/timed-out
attempts. Across direct transports there were 146 candidate-failure warnings
between 10:42 and 13:18 UTC. This is consistent with the reported old
`PeerSync error: SocketException` toast: that app version promoted connection
warnings to user-facing errors. The logs show failures of individual network
paths, not a fatal service error. The source repair now keeps these warnings
in the journal without turning them into dashboard error notifications. The
bundle contains service logs only, so it cannot verify the third PC's actual
post-upgrade rendered notifications.

The earlier run also recorded 4,154 `transfer.block_not_found` warnings,
paired with 4,154 `transport.block_request_unavailable` responses and 1,927
`transfer.block_peer_unavailable` warnings, mostly from 10:54–11:59 UTC; the
last pair occurred at 13:18 UTC. In current source, `block_not_found` means
the serving peer's published index no longer identifies a file chunk or
complete directory-graph block for the requested hash and length. The
sanitized log does not identify those block hashes or paths, so it cannot
attribute the churn to a particular file or prove that it was delivered.

A second service run began at 18:59:35 UTC and contributed 2,981 events
through collection at 19:10. It recorded eight authenticated transport
sessions (three QUIC, two TCP, three Relay) and selected TCP, QUIC and Relay
candidates. Four folder sync passes used Relay. Twelve direct-candidate
failures were warnings; two public TCP failures carried native `10051`.
There were **no** `Error`-level events and **no** block-not-found or
block-peer-unavailable events in this 11-minute run. This establishes an
authenticated fallback relay session on the physical mobile-network PC,
but not a transferred file over relay, a forced-direct-failure test, or a
long-duration clean run. The collector's launch path alone does not confirm
the installed PeerSync executable version.

At the first PC, a read-only query of the live physical-folder database
found 21 local index paths, 21 remote paths from each of the two connected
devices, zero needed-work rows and zero pull-retry rows at inspection time.
Neither the local nor remote index contains `third-pc-check.txt`, and that
file is absent from the first PC's native folder. This does not establish
whether it was created in the third PC's actual joined folder. A new
50-byte `outside-pc-20261024.txt` was placed in the first PC's physical test
folder at 19:19 UTC; its SHA-256 is
`A3C33CC68FB65F591D79CEAC7B94B96EA421556AA6527C42AC7A920AD837407A`.
The first PC indexed this new path locally and received an index record for
it from each of the two connected devices; the needed-work table remained
empty. The operator then verified the native file at
`C:\Users\offic\Documents\PeerSync-WAN-Test\outside-pc-20261024.txt` on the
third PC with `Get-FileHash`; its SHA-256 matched the first PC's value above.
That closes this specific byte-for-byte first-to-third delivery check. The third PC
had been reconnected to Tailscale after diagnostic collection and was on
Tailscale when the hash was checked. This run did not isolate the native WAN
path, so it cannot close the NAT or forced-relay gates.

The independent hotspot probe also passed correlated HTTP/2/TCP and
HTTP/3/UDP rendezvous checks with `productionSyncAvailable=true` and
connected, clock-accepted peers. These results close the diagnostic gap for
the original socket warning, show actual relay authentication, and verify
one file's bytes on the first and third PCs while Tailscale was available.
They do not close Gate P4's native NAT matrix, Tailscale-free WAN file
convergence, forced relay byte-transfer/load check or soak.

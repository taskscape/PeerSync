# Second-PC mobile-hotspot observation, 2026-10-28

The operator confirmed that the second PC `DESKTOP-BN535U0` was connected
through the Samsung Galaxy mobile hotspot. The final PeerSync field test
started before the 14:00 Europe/Warsaw cutoff and ran from
`2026-10-28T12:58:35.1784682Z` to `13:03:35.2434804Z` on its report clock.
All three clients used installed version 1.0.43.0. The first PC stayed on
its original network; the second PC's Ethernet was disabled for the test.
Its only active physical adapter was Wi-Fi (`ZAG6`, `192.168.10.152`), its
route to the rendezvous server `45.11.63.194` selected Wi-Fi, and Tailscale
remained available for management.

The tracked `Test-NativeWanWithoutOverlay.ps1` helper was staged on the
second PC with matching SHA-256
`FAEA7A4F88DAFDF9E1890B3CA9658173F3B1C16FF9031EFCC27FE37D72067E6A`.
It placed program-scoped inbound and outbound Windows Firewall rules on the
Tailscale interface for `PeerSync.Service.exe` only, then left them active
for 300 seconds. A read-only remote query during that window found both
rules present, PeerSync Running, Wi-Fi as the sole active physical adapter
and the server route on Wi-Fi. `psp peers --json` on the second PC showed
authenticated peers selecting `Relay` during the window.

The second PC created
`native-wan-second-20261028T125835Z-bcfe9a1984bd.txt`; it arrived in the
first PC's native joined folder before the isolation window ended with
matching SHA-256
`F3DF6593CDCCB210724CC12A72F860E8291B0CF6833FB65790939FDF3A6F01B4`.
The first PC created a fresh 1 MiB
`native-wan-first-20261028T1259Z.bin`; it arrived on the second PC during
the same window with matching native SHA-256
`463C378630E5E8B91D3C8781E1158D20534C5FFA422FDBE42FC3E6E38275F29D`.
This verifies two-way file bytes over the separate-network relay path while
PeerSync's Tailscale traffic was blocked.

The helper ended with exit 0 and reported `failure=null`, both temporary
rules removed, its recovery task removed and the service Running after
cleanup. The exact report
`artifacts/field/physical-50k-20261027/NativeWAN-result-bcfe9a1984bd.json`
was copied from the second PC with matching source/destination SHA-256
`ED170C363DF4BD8612C443D7224E24CA2B447B5E10D34F4A3A6CE9C6165CA98C`.
Ethernet was re-enabled, the server route again selected Ethernet, all
three network adapters were Up, PeerSync stayed Running, the two test
firewall rules were absent and the extra Ethernet recovery task was
removed. The second PC's zero-sleep AC/DC policy was retained as requested.

This five-minute observation demonstrates one physical mobile-hotspot
route and relay-backed, two-way native file sync. It is not the required
three-hour WAN soak, a four-snapshot native-tree comparison, the full
two-home-NAT matrix or a relay load/rate-cap result. Gate P4 and the
release gate remain open.

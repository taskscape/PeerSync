# Port-mapping lifecycle, 2026-10-23

The installed peer runtime already enabled UPnP/NAT-PMP mapping for its TCP
and UDP listener, but its map renewal ran only on a fixed 30-minute timer and
advertised a returned endpoint indefinitely. It could also queue repeated
mapping attempts for one rediscovered gateway. Current source bounds the
external-address and mapping calls, coalesces those duplicate attempts,
records per-protocol lease deadlines, and omits expired mappings from public
endpoint announcements. The renewal loop checks cheap local deadlines every
five seconds, contacting a gateway at half its returned lease. For a normal
one-hour lease this is the existing 30-minute renewal point; a shorter lease
is renewed sooner. A failed address lookup withdraws the previous external
address, and gateway errors leave independent mappings and future renewals
running. Stop waits for active operations and removes both owned protocol
mappings with bounded gateway calls.

Local fake-gateway tests cover TCP/UDP mapping and cleanup, returned lease
expiry, half-life renewal, duplicate discovery, one-protocol failure and a
failed/recovered external-address lookup. They do not prove mapping on a real
UPnP or NAT-PMP router. The deferred P0.2.2 physical NAT matrix and deployed
P4 gate therefore remain open.

The local Net suite passed 55/55 and the Release solution build passed with
zero warnings and errors. No real gateway was used in this check.

# P4 port-mapping retry isolation, 2026-10-24

`PortMapper` previously renewed both TCP and UDP whenever either mapping was
due. A router refusing UDP left that protocol perpetually due, so its five-
second poll also recreated the healthy TCP mapping every five seconds. A
failed external-address lookup likewise caused repeated gateway requests.

The mapper now tracks a renewal deadline per protocol and a separate external-
address refresh deadline. Successful one-hour leases renew at half-life;
failed requests retry after 30 seconds. A failed address lookup withdraws the
advertised endpoint until a later successful lookup. If a protocol's lease
expires after a failed renewal, the mapper signals the tracker to withdraw
that candidate once. Duplicate discovery announcements before a deadline do
not contact the gateway again.

The focused `PortMapperTests` cases include one-protocol refusal and recovery,
short returned leases, stale-address withdrawal, expiry notification and
bounded cleanup. The complete Net suite passed 59/59 with
no skips. Its TRX is under
`artifacts/tests/port-mapper-renewal/`. This is a fake-gateway check; real
UPnP/NAT-PMP router behavior and the native NAT matrix are still open.

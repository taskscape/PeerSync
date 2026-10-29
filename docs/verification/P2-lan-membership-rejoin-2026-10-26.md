# LAN multicast membership refresh, 2026-10-26

Windows can discard a socket's multicast membership when an adapter goes down
and back up while assigning the same interface index. The previous LAN host
remembered joined IPv4 addresses and IPv6 indices indefinitely, so its
`NetworkAddressChanged` callback could skip the necessary rejoin. It also
aborted the rest of a refresh when one adapter's properties could not be read.

The host now builds the current IPv4 and IPv6 interface-index sets on every
refresh, drops its old memberships and re-joins each active multicast-capable
interface. A departed interface is removed from local state even if Windows
already dropped it. A failed join remains eligible for the next refresh. A
single adapter-property failure no longer skips the other adapters. Debug
events contain only desired/changed counts for each address family; warnings
contain a socket error category, never adapter names or addresses.

The current-source Release Net project built with zero warnings/errors. The
focused discovery class passed **7/7** tests, including a regression that
rejoins an unchanged index, removes a departed index and retries a failed
join. The class also ran two actual socket instances and found both peers over
an active non-loopback interface within five seconds. The full Release Net
suite passed **60/60**, zero skips. The initial `dotnet test --filter` attempt
discovered zero tests because this xUnit v3 executable uses its own `-class`
filter; the direct runner passed. This local result does not establish an
installed-service Wi-Fi switch, each physical adapter's path or Server-side
multicast. P2.3.1 and Gate P2 remain open for those field checks.

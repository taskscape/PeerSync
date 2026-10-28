# Relay per-device budget across both directions, 2026-10-28

The P4 relay cap is 2 MB/s **per device**. Previously `RelayRegistry`
charged a forwarded chunk only to its sender's token bucket. Two devices
sending to each other could each use a full allowance while also receiving,
so each endpoint could carry twice its intended combined relay traffic.

The registry now charges each forwarded byte to both endpoint devices. It
acquires their buckets in stable DeviceId order, followed by the shared server
bucket, so opposite streams do not each hold one endpoint's tokens while
waiting for the other. A same-device pair is charged once. The existing
rate-slice logic and five-minute activity refresh remain in place.

The Release `RelayRegistryTests` class passed **7/7**, including a new
opposite-direction regression: after one 64 KiB transfer exhausts both
endpoint budgets, a reverse 64 KiB transfer cannot complete in the initial
200 ms, even though the server's total bucket still has capacity. The first
attempt used `dotnet test --filter` and discovered zero tests because the
xUnit v3 runner requires `--filter-class`; it is not a passing result. The
correct focused command was:

```powershell
dotnet test tests/PeerSync.Rendezvous.Tests/PeerSync.Rendezvous.Tests.csproj `
  -c Release --no-restore `
  --filter-class PeerSync.Rendezvous.Tests.RelayRegistryTests
```

This is local source-level quota evidence. The currently installed physical
Rendezvous service predates this change. Its upgrade, provider-uplink sizing,
deployed simultaneous-flow measurement, central/client diagnostic correlation,
and the physical WAN/NAT gates remain open. The ongoing three-PC LAN soak
must finish before changing the central service used by that run.

The complete Release Rendezvous test project passed **25/25**, zero skipped,
after the repair. A self-contained, development-signed x64 server installer was
built from commit `b7e4b67f839d03101f1ac51670f70748d1b46c84` as version
`1.0.42.0`. Its SHA-256 is
`3B4A7318706EC3E4F6B168776D2E6795735DBD1DA99CCD97CD0C48A074E0FDEC`,
matching the adjacent manifest. Authenticode is valid with a timestamp and
development signer `6AE1CBFC31F1DB189AF03A82D60C535AE8E0A7BD`. The
manifest marks it `localDevelopmentOnly`; the physical server has not yet been
upgraded and this does not satisfy a production signing gate.

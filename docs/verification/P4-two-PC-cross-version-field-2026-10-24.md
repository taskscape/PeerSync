# Physical two-PC cross-version sync, 2026-10-24

The first PC runs installed PeerSync 1.0.21.0 after its keepalive upgrade. The
known second PC at `100.88.55.83` is still running 1.0.16.0; its already
authorized administrative share permitted direct file and journal checks
without changing Windows remoting configuration. Its joined folder is native
storage on the second PC, accessed through `C$` for this field check.

The three existing test text files matched by SHA-256 on both machines before
the new writes. The first PC then created `first-pc-1.0.21-check.txt`; it was
present on the second PC at the first check with the same SHA-256,
`2F4834A41D8E7166FB50BF64324ED991715F39786F58E302B4106DD415F77669`.
The second PC then created `second-pc-1.0.21-check.txt` through its native
joined folder; it arrived on the first PC in about three seconds with matching
SHA-256, `698256E95DF40ECE3E42F502E554D6EE0B32E7E45D90046753735D80149078DF`.
The two new files are small, contain only test text and remain in the joined
folders as field evidence. This verifies bidirectional file convergence across
the two installed versions and the real LAN-connected PCs. It is not a
three-PC 50 GB check, native NAT proof or 72-hour soak. This file check did not
identify which PeerSync transport path carried the bytes; the separate native
LAN QUIC spike provides that path evidence for its own transfer.

The second PC's 1.0.16.0 journal also shows repeated tracker stream failures
and a 256-second reconnection wait at its saturated backoff. The
[tracker analysis](P4-tracker-session-correlation-2026-10-24.md) records the
source-level retry-accounting repair. This cross-version file result does not
mean the older tracker client is healthy or that the third-PC hotspot route
has converged.

The second PC was subsequently upgraded to development-signed client 1.0.22.0
from source `c74a5d5`. Its installer SHA-256 is
`461489100474B6A58E1D6A3C0542CFBDC56A4B91217CB0460EA6CD14F24CC5F1`,
and its Authenticode signature was Valid. The first silent setup attempt
stopped the service but could not replace the open interactive `PeerSync.exe`
dashboard (`DeleteFile` access denied, setup exit 5). The service was
immediately restarted. After closing only that dashboard process, the same
signed setup completed with exit 0. The one-time upgrade task was removed.
The dashboard can be reopened in the interactive user session; background
sync does not require it.

The second-PC service is Running with installed version 1.0.22.0. All four
checked installed binaries have Valid signatures and match their published
payload hashes. The settings SHA-256 remained
`0C973DEA01A96A6BAC1447152C853983A0A3B7B9020742A3B0D60640D57E921D`,
and all five pre-upgrade test-file hashes were unchanged. The before/after
snapshots are retained under
`artifacts/field/second-pc-retry-upgrade-20261024/`; the setup log remains on
the test PC at `C:\Temp\PeerSync-Field-c74a5d5\setup.log`.

After that upgrade, `second-pc-1.0.22-check.txt` arrived on the first PC in
about five seconds with matching SHA-256
`1559C52C55A61125E04965E723DCBA67D02A9AD7E5ABC9C313067E658EACF349`.
The first PC's `first-pc-to-1.0.22-check.txt` arrived on the second PC in
about three seconds with matching SHA-256
`037A460E4DE184B3872421C5DC9EBA3E07FE16BDF9D6EF70A881394A60ED4CB9`.
The updated second-PC journal contains a new tracker connection and
server-issued session ID. These observations verify post-upgrade two-way
sync, but a controlled long-session reset has not yet exercised the new
retry-accounting branch. The third-PC socket error and file convergence
remain open.

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

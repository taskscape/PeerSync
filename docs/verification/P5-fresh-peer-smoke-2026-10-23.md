# P5.3 fresh-peer scale runner smoke, 2026-10-23

`MillionPeerBenchmark` reindexes a retained real scanner corpus, starts two
production `TransportFolder` coordinators on separate roots and databases,
connects them through the trusted in-memory test transport, and checks every
fresh-peer pathname and byte after both engines settle. It retains the peer
tree, SQLite databases, correlated diagnostic logs and machine-readable
runner provenance. The transport does not substitute for the separate
physical LAN/WAN gates.

The initial direct 1,000-file smoke synced all 1,000 one-byte files and
indexed 1,001 destination rows in four convergence rounds, 44.133 seconds.
The wrapper `build/Run-P5-PeerScale.ps1` then passed a second retained
1,000-file fixture in four rounds, 36.067 seconds, with exact native-byte
verification. Its evidence is
`artifacts/scale/p5-peer-20261022T232547-49f710296c1c4eb5bae19cae07d170a1`.
The wrapper recorded source revision, dirty-state flag, binary SHA-256,
requested file count and final scanner/peer reports. This smoke used an
uncommitted runner working tree, so it is a control for the harness rather
than final million-file gate evidence.

Next run the committed-source wrapper against the retained million-file
corpus. Keep all failures and logs; do not infer a million-file pass from
the 1,000-file control.

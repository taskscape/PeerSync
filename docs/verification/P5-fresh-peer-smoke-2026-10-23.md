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

The first committed-source million-file attempt passed its scanner target
but was stopped after 50 of roughly 1,000 bounded source-publication passes.
At about 11 seconds per pass, its original four-hour whole-run ceiling left
insufficient room for fresh-peer application and native verification. Its
incomplete output and logs remain under
`artifacts/scale/p5-peer-20261022T232743-5ae652c974354447a1e06f39343bca31`
and the fixture's `source-peer-logs-*` directory. The benchmark ceiling is
now 12 hours, matching the scale guide's willingness to give a smaller
100,000-file five-node corpus 12 hours. The stopped attempt is not a failed
engine assertion and is not a million-file peer pass.

The stopped wrapper's `benchmark.log` remained empty despite visible console
progress, because its pipeline buffered the file until normal completion.
The runner now appends and flushes each progress line as it arrives. A
1,000-file `-NoBuild` control passed in four rounds (41.951 seconds), and
the live log had 1,031 bytes before the process exited. Its retained
evidence is `artifacts/scale/p5-peer-20261022T235636-f8781ebd002b43d7a6ca93ffdf001658`.
The already-running 12-hour million-file process started before this wrapper
edit, so its source and destination diagnostic journals remain the reliable
live evidence until the wrapper exits.

The progress report now includes path-only counts of source and destination
directory publications and the pass's visited, deferred, recovered and scan
counts. This makes a shrinking publication backlog distinguishable from a
repeated scan without reading raw diagnostic files. A separate-output build
passed with zero warnings while the million-file process kept its original
binary. Its 1,000-file retained-corpus control passed: 1,000 native bytes
verified, 1,001 peer rows, four convergence rounds and 39.870 seconds. The
new fields showed one pending destination directory at convergence round zero
and none at completion. The control log is retained locally at
`artifacts/scale/progress-runner-smoke.log`; the running million-file binary
predates these fields and remains the actual gate candidate.

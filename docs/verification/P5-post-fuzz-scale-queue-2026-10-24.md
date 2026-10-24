# P5 million-file post-fuzz run queued, 2026-10-24

The P5.3 fresh-peer gate is **not yet passed**. The run is queued after the
uncontended 24-hour fuzz campaign, avoiding a scale measurement under its CPU
load. No scanner or peer result is claimed here.

`tests/PeerSync.Benchmarks` built in Release with locked restore, zero warnings
and zero errors, from revision `dafd167690888daa30c659265422711074332b87`.
The benchmark inputs had no uncommitted edits. Its 58 output files were copied
to an immutable run directory at
`artifacts/scale/queued-after-fuzz-20261024/bin` before scheduling. The
`PeerSync.Benchmarks.exe` SHA-256 is
`5FAB8864FFC04D24ED7767F4594C7F450AE70D66DE8F8ADDAD6E923E8FCD714A`;
`prepared.json` retains the source, hash, fixture, UTC preparation time and
free-space observation.

At repository head `2380b02`, a source diff from `dafd167` is empty for the
benchmark, Core, Crypto, Protocol, Storage, Engine and `PeerSync.Net/InMemory`
paths used by this scenario. Later ConnectionManager and PortMapper changes
are outside the in-memory scale transport. This establishes unchanged relevant
inputs for the queued fixed binary, not a whole-repository current-head build.

At later head `c4437d0`, the only diff within those input directories is
`Core/Diagnostics/DiagnosticBundleCollector.cs`. Only the CLI and rendezvous
diagnostics commands call it; the benchmark and in-memory scan/peer paths do
not. New tracker diagnostics likewise do not change this benchmark. The
queued binary remains a scoped measurement of the current scale execution
path, subject to its terminal and native-byte checks; it is not a build of
every current repository file.

The retained fixture is
`artifacts/scale/p5-20261022T223804-81cef95f6d244dc09c796b053430f8be-fixture`.
It contains the million-file native source tree and canonical `index.db`.
The queued run invokes `--scan-peer-existing <fixture> --files 1000000`, so
it measures current-source first indexing and drives the production source
and fresh-peer coordinators over the benchmark's in-memory test transport.
The benchmark verifies every destination native byte and path. This does not
substitute for a physical million-file transfer.

`PeerSync-P5-Scale-After-Fuzz-20261024` was registered and started as a hidden
SYSTEM scheduled task with a 76-hour execution limit. Its retained
`run-after-fuzz.ps1` waits for
`artifacts/fuzz/p5-24h-20261024T090449Z/terminal.json`; it refuses to start
unless that status is `passed`, its immutable executable hash still matches,
the source tree exists and C: has at least 6 GiB free. It requests system-awake
while waiting and running. During the benchmark it stops the exact child if
free space drops below 1.5 GiB, preserving the partial fixture for diagnosis.
The run writes separate stdout, stderr, running and terminal records in its
queue directory. A passing terminal record requires exit zero, a correct
scanner result with target met, a correct full fresh-peer result, and empty
stderr. Stopped, blocked or missing terminal evidence cannot close P5.3.

At registration the task was running, and `powercfg /requests` showed its
system-awake request alongside the separate fuzz request. C: had about 7.3 GB
free after the benchmark copy. The test's retained data can grow substantially;
the exact free-space preflight at launch is authoritative. Once both jobs end,
inspect their terminal records and the scheduler results before removing all
three temporary scheduled tasks.

The idle disposable `PeerSync-P3-Server2022-Clean` and
`PeerSync-P3-Server2025-Clean` Hyper-V guests were subsequently shut down with
the normal `Stop-VM` guest shutdown path. Both report `Off`; their disks and
evidence were retained. This stopped differencing-disk writes and raised C:
free space to about 10.3 GB. They can be restarted for later VM checks.

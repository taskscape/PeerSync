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

## Low-disk watcher recovery, 2026-10-25

During the 24-hour fuzz run, C: free space fell below the unchanged 6 GiB
scale-start threshold. The queued watcher originally wrote a terminal
`insufficient-start-disk` result immediately after fuzz passed. Its retained
`run-after-fuzz.ps1` now waits up to six additional hours for C: to recover
to at least 6 GiB, writing a bounded `disk-wait.json` observation every five
minutes. If the threshold is never met, it writes the original blocked
terminal result with the deadline. The 1.5 GiB in-run abort, exact prepared
binary hash, passing-fuzz requirement and all benchmark success criteria are
unchanged. This is only a launch delay; low disk does not count as a pass.

The revised watcher parsed under PowerShell 7, and its script SHA-256 is
`83376DDA353F34ACD83078AA73A3644B3D4BC6D76CC91C7A9A837D9E51EF90D5`.
Its prepared benchmark binary still matched SHA-256
`5FAB8864FFC04D24ED7767F4594C7F450AE70D66DE8F8ADDAD6E923E8FCD714A`.
Only the waiting scale task was restarted; its new process PID 84052 and
scheduled-task state `Running` were observed while the original fuzz PID
52808 remained live. Neither task has a terminal result yet. The observed
Windows shadow-copy allocation was growing as C: space fell; its cause is
not established here, and no restore snapshot was changed.

At 2026-10-25 08:04 UTC, C: had 80.32 GiB free. Both original fuzz PID
52808 and the waiting scale-watcher PID 84052 remained live. This clears the
current disk-space concern without restarting either run; the watcher must
still observe a passing fuzz terminal before it can launch, and no scale
success is claimed from free space alone.

## Fixed-binary run started, 2026-10-25

The fuzz terminal passed at 09:05:58 UTC. The watcher then verified its fixed
benchmark executable hash and retained fixture, observed 85,746,462,720 free
bytes on C: (well above the 6 GiB start threshold), and launched benchmark
PID 36684 at 09:06:39 UTC. Its `running.json` records the expected SHA-256
`5FAB8864FFC04D24ED7767F4594C7F450AE70D66DE8F8ADDAD6E923E8FCD714A`
and the unchanged 1.5 GiB continue threshold. At launch, no scanner or peer
result was claimed from the process state alone.

The scanner row subsequently completed before the fresh-peer stage. For its
1,000,000 real one-byte files in 1,000 directories, first index took
229.955197 seconds, the unchanged rescan took 61.7057197 seconds with zero
rehashes and updates, idle CPU was 0.0086803%, and 1,167 samples recorded a
134,856,704-byte peak working set and 86,011,904-byte sampled peak private
memory. Its 1,001,000 rows, sequence preservation, bounded changed paths,
`correct=true` and `targetMet=true` pass the scanner targets. The peer stage
and overall terminal remain pending. The scanner result names an additional
run-specific database and diagnostic directory inside the retained fixture.

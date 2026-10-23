# P5.3 directory-publication stage timing, 2026-10-23

The publication worker now emits debug-only `directory.publication_time_*`
events for preparation, capture, rebase, parent-root proof, publication
preparation and commit. Each event has elapsed milliseconds and the queue
session ID, with no path, content hash, FolderId or key. The events are
diagnostics; the bounded capture and durable block-proof protocol are
unchanged.

A retained 10,000-file control used two production `TransportFolder`
coordinators over the trusted in-memory test transport and the native corpus
at `artifacts/scale/p5-leaf-cache-10k`. The run's source/binary hashes,
logs and databases are under
`artifacts/scale/p5-publication-timing-10k-20261023/`. It completed in
510.940 seconds with 10,000 native file bytes and paths verified, 10,010
destination rows, zero needed rows and `correct=true`. The source finished
its ten directory publications in 135.558 seconds. Their timed stages were:

| Source stage | Total for ten directories |
| --- | ---: |
| Capture | 91,749.6 ms |
| Prepare | 45.1 ms |
| Parent-root proof | 8.8 ms |
| Publication preparation | 9.6 ms |
| Commit | 34.8 ms |

The capture stage dominates the measured per-directory work. Source code
shows that each signed child becomes a blob and record block with guarded,
durable capture proofs; the timing does not by itself apportion the capture
cost between native I/O, flushes, hashing, and signature verification.

An experimental build reused an already authenticated `VersionView` inside
capture, removing repeated signed-record opens without changing the wire
format. Its focused directory suite passed 251 cases, and an isolated full
Engine rerun passed 1,038 cases. A preceding full run under additional
10,000-file benchmark load had five intermittent native-root fixture errors;
there was no code change between that run and the green rerun. The paired
source-publication control took 135.843 seconds, with 96,399.0 ms in capture
for ten directories. This one contended comparison does not establish a
speedup. The experimental process was intentionally stopped after source
publication, before native-byte verification, and the verified-view edit was
reverted. Its source patch, binary hashes and partial output remain under
`artifacts/scale/p5-publication-reuse-10k-20261023/`.

The committed-source million-file candidate and 24-hour fuzz process stayed
alive during these controls. The [bounded scanner staging design](../proposals/P5-bounded-scanner-staging.md)
addresses the separate 2.55 GB peak-memory finding; neither this diagnostic
nor the stopped experiment closes P5.3.

# P5.3 million-file scan, contended local SSD, 2026-10-23

The first full scanner run used committed revision
24d65a42bfa8b69199d96504d91cc4f64da0f24c with a clean tracked tree
and benchmark SHA-256
AA2063A5CF42D6CE30264F4E97DF511F2485C86FA289ED1478425F08DB889E8C.
The C: drive is reported as SSD model CT2000P310SSD8. The generated corpus
and machine-readable evidence remain under
artifacts/scale/p5-20261022T223804-81cef95f6d244dc09c796b053430f8be.
The separate 24-hour fuzz process was running at lower priority, so this is
a contended measurement.

| Measure | Result | P5.3 target |
| --- | ---: | ---: |
| Real one-byte files generated | 1,000,000 | 1,000,000 |
| Corpus creation | 301.544 s | reported separately |
| First index including database open | 229.087 s | under 1,200 s |
| Files hashed on first index | 1,000,000 | 1,000,000 |
| Indexed rows including directories | 1,001,000 | expected |
| Unchanged scan | 8.482 s; zero rehashes/updates | correctness |
| Idle CPU over 30 seconds | 0.00434% of 12 logical processors | under 1% |
| Idle working set | 2,570,678,272 bytes | under 300 MiB |
| Idle private bytes | 2,529,640,448 bytes | under 300 MiB |

The first-index time, idle CPU, row count and unchanged correctness passed.
Idle memory failed the 300 MiB target, so the runner returned failure and
retained the corpus and SQLite database. A diagnostic reindex of that
existing corpus took 228.390 seconds for its first index and again passed
unchanged correctness. Before forced collection, managed memory was
2,272,351,992 bytes and private process memory was 2,489,868,288 bytes.
After two full compacting collections, managed memory fell to 56,635,696
bytes, but private process memory remained 2,079,977,472 bytes and working
set remained 2,121,302,016 bytes. That diagnostic binary had uncommitted
instrumentation and is not a gate pass. A second diagnostic pass measured
2,467,729,408 bytes of committed GC heap before collection and
1,659,273,216 after it, while SQLite reported only 9,042,528 bytes.
The 1,000,000-file reindex again passed correctness in 200.983 seconds,
but its 2,555,904,000-byte idle working set failed memory. This points to
committed GC heap segments, not SQLite cache. A third diagnostic with
large-object-heap compaction also failed: first index 197.555 seconds,
idle working set 2,556,973,056 bytes, post-collection private bytes
1,751,994,368, committed GC heap 1,706,471,424 and live GC heap
70,533,072. Compaction alone did not return the committed segments.
An aggressive decommit diagnostic returned the process to 133,836,800
working-set bytes and 86,343,680 private bytes, but the ordinary idle
sample before that diagnostic collection still failed. Collecting within
`Scanner.ScanAsync` also failed to reclaim the completed async scan's
temporary state. A deferred, coalesced reclaim after the scan task unwinds
now performs two full compacting collections and an aggressive decommit
only after a walk of at least 100,000 entries, ten seconds of quiet, and
a private-memory reading of at least 300 MiB. Events share the original
scan session ID and report scheduled, skipped, completed or failed work.

An instrumented local rerun of the retained million-file corpus passed the
scanner benchmark: first index 189.982 seconds, unchanged walk 6.296
seconds with zero hashes/updates, idle CPU 0.2473%, idle working set
135,372,800 bytes and private bytes 85,696,512. The complete reclaim took
867 ms and reported 2,400,100,352 committed GC bytes released. Peak
working set was still 2,544,041,984 bytes. The benchmark log is
`artifacts/scale/p5-large-reclaim-final-diagnostic-20261023.log` and its
session-correlated events are in the retained `logs-e10ce6988ccc42438f18803ae075dc24`
folder. This run used an uncommitted diagnostic working tree; a final
committed-source gate run was then performed. Commit
`73f90588b6df23e4ae4ce0021aa77f65e6168038` and benchmark SHA-256
`6E140295317F78A4E08A18208272CC73C58ECD42F8831CB7DC73D3F1600AD0BC`
indexed all million files in 226.529 seconds, then found 1,001,000 rows
unchanged in 6.880 seconds with zero rehashes, updates or sequence change.
Idle CPU was 0.2474%, working set 135,827,456 bytes and private bytes
86,089,728; peak working set was 2,545,479,680 bytes. The retained
scanner and running fresh-peer evidence are in
`artifacts/scale/p5-peer-20261022T234611-e6deecf51d6b416f8716ec5614bf42fe`.
The million-file fresh-peer result is still pending.

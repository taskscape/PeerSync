# Bounded scanner prior-path merge, 2026-10-25

The post-fuzz million-file run reached a full source rescan during directory
publication. Its fixed binary emitted `scan.memory_enumerated` for 1,001,000
native entries, then spent more than nine minutes before the next scanner
stage event. The process and watchdog remained live; the process's active
thread was observed waiting on page-in I/O. This observation locates a slow
stage but does not by itself prove which operation within that stage dominates.

Source inspection found two passes that stream every prior indexed path and
issue an individual observation-database lookup for each path. The scanner
now reads prior paths and observed keys in matching SQLite binary UTF-8 order,
using at most 1,000 prior paths per observation query. Each read cursor closes
before the caller stages rename candidates or deletion updates. Missing paths
still go through the existing protection, ignore, rename and tombstone rules;
unencodable native names retain their separate UTF-16 lookup. A sanitized
debug event reports the number of prior paths compared every 100,000 paths.

Local Release build: zero warnings/errors. The focused scanner class passed
21/21 cases, including a new Unicode-order insertion/deletion check and the
existing 1,001-path page boundary, rename and tombstone cases. The complete
Engine suite passed 1,046/1,046 cases. Results are retained locally as
`artifacts/tests/scanner-prior-merge.trx` and
`artifacts/tests/engine-prior-merge.trx`. The real-engine simulation runner
passed 17/17 methods with eight generated flat cases, four generated
hierarchical cases and all ten retained hierarchical regressions. Its TRX is
under `artifacts/tests/simulation/` and its scenario evidence is under
`artifacts/simulation/20261025T094449-22707f027ba145cb91e402b60d0477f5/`.

The concurrent fixed-binary million-file run predates this source change and
is not its performance proof. P5.3 remains open until a build containing the
merge completes the full million-file scanner and fresh-peer native-byte gate.
